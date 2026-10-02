import { api, ApiError, http } from '../../api/http';
import type { BeginUploadRequest, SaveDto, UploadProgressDto, UploadStartedDto } from '../../generated/generated';
import { sha256OfBlob, type Sliceable } from './sha256';

/** A file the user picked (`File` satisfies it). */
export interface UploadFile extends Sliceable {
  readonly name: string;
  readonly lastModified: number;
  slice(start: number, end: number): Blob;
}

export type UploadPhase = 'hashing' | 'uploading' | 'finalizing' | 'done';

export interface UploadProgress {
  phase: UploadPhase;
  /** Bytes hashed (phase hashing) or bytes the server has stored (uploading). */
  done: number;
  total: number;
  /** True when this run continued an earlier, interrupted upload. */
  resumed: boolean;
}

export interface KeyValueStore {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

interface Remembered {
  uploadId: string;
  sha256: string;
}

const KEY_PREFIX = 'x4mp.saveUpload.';
const MAX_RETRIES = 5;

/** The page keeps the upload id (and the hash, so a reload does not rehash) per file identity in localStorage. */
const fileKey = (f: UploadFile) => `${KEY_PREFIX}${f.name}|${f.size}|${f.lastModified}`;

function defaultStore(): KeyValueStore | null {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null; // blocked storage: the upload still works, it just cannot be resumed by id (the server also resumes by hash)
  }
}

function safe<T>(fn: () => T): T | null {
  try {
    return fn();
  } catch {
    return null;
  }
}

function readRemembered(store: KeyValueStore | null, f: UploadFile): Remembered | null {
  const raw = store ? safe(() => store.getItem(fileKey(f))) : null;
  if (!raw) return null;
  try {
    const v = JSON.parse(raw) as Partial<Remembered>;
    return typeof v.uploadId === 'string' && typeof v.sha256 === 'string' ? { uploadId: v.uploadId, sha256: v.sha256 } : null;
  } catch {
    return null;
  }
}

export interface UploadOptions {
  onProgress?: (p: UploadProgress) => void;
  signal?: AbortSignal;
  store?: KeyValueStore | null;
  /** Overrides the server's chunk size (tests). */
  chunkSize?: number;
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Asks the server where the upload stands; null when the upload id is unknown (the server restarted or it was aborted). */
async function resumePoint(uploadId: string): Promise<UploadProgressDto | null> {
  try {
    return await api.get<UploadProgressDto>(`/api/v1/saves/uploads/${uploadId}`);
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) return null;
    throw e;
  }
}

/**
 * Resumable chunked upload (server-design 3.4). Hashes the file, then either continues the upload remembered for this file
 * (GET the server's offset) or begins one (the server also resumes by hash, so a lost id costs nothing). Chunks go as
 * `PUT` with `Content-Range`; any failure re-asks the server for its offset and carries on from there. Aborting via the signal
 * leaves the server's part file and the remembered id in place, so a later run, even after a page reload, resumes.
 */
export async function uploadSave(file: UploadFile, opts: UploadOptions = {}): Promise<SaveDto> {
  const { onProgress, signal } = opts;
  const store = opts.store === undefined ? defaultStore() : opts.store;
  const emit = (phase: UploadPhase, done: number, resumed: boolean) => onProgress?.({ phase, done, total: file.size, resumed });
  const aborted = () => new DOMException('Aborted', 'AbortError');

  let remembered = readRemembered(store, file);
  const sha256 =
    remembered?.sha256 ?? (await sha256OfBlob(file, (done) => emit('hashing', done, false), signal));
  if (signal?.aborted) throw aborted();

  let uploadId: string | null = null;
  let offset = 0;
  let chunkSize = opts.chunkSize ?? 8 * 1024 * 1024;
  let resumed = false;

  if (remembered) {
    const point = await resumePoint(remembered.uploadId);
    if (point && point.size === file.size) {
      uploadId = remembered.uploadId;
      offset = point.receivedBytes;
      resumed = offset > 0;
    }
  }
  if (!uploadId) {
    const body: BeginUploadRequest = { fileName: file.name, size: file.size, sha256 };
    const started = await api.post<UploadStartedDto>('/api/v1/saves/uploads', body);
    uploadId = started.uploadId;
    offset = started.receivedBytes;
    chunkSize = opts.chunkSize ?? started.chunkSize;
    resumed = offset > 0;
  }
  remembered = { uploadId, sha256 };
  if (store) safe(() => store.setItem(fileKey(file), JSON.stringify(remembered)));
  emit('uploading', offset, resumed);

  let failures = 0;
  while (offset < file.size) {
    if (signal?.aborted) throw aborted();
    const end = Math.min(offset + chunkSize, file.size);
    try {
      const res = await http<UploadProgressDto>(`/api/v1/saves/uploads/${uploadId}`, {
        method: 'PUT',
        headers: { 'Content-Range': `bytes ${offset}-${end - 1}/${file.size}`, 'Content-Type': 'application/octet-stream' },
        body: file.slice(offset, end),
        signal,
      });
      offset = res.receivedBytes;
      failures = 0;
      emit('uploading', offset, resumed);
    } catch (e) {
      if (signal?.aborted || (e instanceof DOMException && e.name === 'AbortError')) throw aborted();
      // Offset mismatch, a dropped connection or a short body: the server kept what arrived, so ask where to continue.
      if (++failures > MAX_RETRIES || (e instanceof ApiError && e.status !== 409 && e.status !== 400 && e.status < 500)) throw e;
      await sleep(Math.min(200 * failures, 1000));
      const point = await resumePoint(uploadId);
      if (!point) throw e;
      offset = point.receivedBytes;
    }
  }

  emit('finalizing', file.size, resumed);
  try {
    const saved = await api.post<SaveDto>(`/api/v1/saves/uploads/${uploadId}/complete`);
    if (store) safe(() => store.removeItem(fileKey(file)));
    emit('done', file.size, resumed);
    return saved;
  } catch (e) {
    // The server drops the part (and the id) on a hash mismatch or a non-save: forget it so a retry starts clean.
    if (e instanceof ApiError && e.status === 422 && store) safe(() => store.removeItem(fileKey(file)));
    throw e;
  }
}

/** Drops a remembered upload and tells the server to delete its part file. */
export async function discardUpload(file: UploadFile, store: KeyValueStore | null = defaultStore()): Promise<void> {
  const r = readRemembered(store, file);
  if (store) safe(() => store.removeItem(fileKey(file)));
  if (r) await api.delete(`/api/v1/saves/uploads/${r.uploadId}`).catch(() => undefined);
}
