import { afterEach, describe, expect, it, vi } from 'vitest';
import { uploadSave, type KeyValueStore, type UploadProgress } from './uploader';

async function shaHex(data: Uint8Array): Promise<string> {
  const d = await crypto.subtle.digest('SHA-256', data as BufferSource);
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

class MemStore implements KeyValueStore {
  m = new Map<string, string>();
  getItem = (k: string) => this.m.get(k) ?? null;
  setItem = (k: string, v: string) => void this.m.set(k, v);
  removeItem = (k: string) => void this.m.delete(k);
}

/** A stand-in for the server's resumable upload endpoints (server-design 3.4). Its state outlives a "page reload". */
class FakeSaveServer {
  parts = new Map<string, Uint8Array>();
  uploads = new Map<string, { sha: string; size: number }>();
  calls: string[] = [];
  puts: { start: number; end: number }[] = [];
  completed: string | null = null;
  /** Abort the page's signal after this many chunks were stored. */
  stopAfterChunks: { n: number; ctl: AbortController } | null = null;

  install() {
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string, init: RequestInit = {}) => this.handle(url, init)),
    );
  }

  private async handle(url: string, init: RequestInit): Promise<Response> {
    const method = init.method ?? 'GET';
    this.calls.push(`${method} ${url}`);
    if (url === '/api/v1/saves/uploads' && method === 'POST') {
      const b = JSON.parse(init.body as string) as { sha256: string; size: number };
      const id = `u${this.uploads.size + 1}`;
      this.uploads.set(id, { sha: b.sha256, size: b.size });
      return json(201, { uploadId: id, chunkSize: 8 * 1024 * 1024, receivedBytes: this.parts.get(b.sha256)?.length ?? 0 });
    }
    const m = /^\/api\/v1\/saves\/uploads\/(\w+)(\/complete)?$/.exec(url);
    if (!m) return json(404, { code: 'NotFound', title: 'nope' });
    const up = this.uploads.get(m[1]!);
    if (!up) return json(404, { code: 'NotFound', title: 'Not found.' });
    const have = this.parts.get(up.sha) ?? new Uint8Array(0);
    if (m[2]) {
      const actual = await shaHex(have);
      if (actual !== up.sha) return json(422, { code: 'HashMismatch', title: 'x', detail: 'bad hash' });
      this.completed = up.sha;
      return json(201, { sha256: up.sha, sizeBytes: up.size, displayName: 'x' });
    }
    if (method === 'GET') return json(200, { receivedBytes: have.length, size: up.size });
    const range = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(new Headers(init.headers).get('Content-Range') ?? '')!;
    const start = Number(range[1]);
    const end = Number(range[2]);
    if (start !== have.length) return json(409, { code: 'OffsetMismatch', title: 'Conflict.', detail: 'continue there' });
    const body = new Uint8Array(await (init.body as Blob).arrayBuffer());
    const next = new Uint8Array(have.length + body.length);
    next.set(have);
    next.set(body, have.length);
    this.parts.set(up.sha, next);
    this.puts.push({ start, end });
    if (this.stopAfterChunks && this.puts.length === this.stopAfterChunks.n) this.stopAfterChunks.ctl.abort();
    return json(200, { receivedBytes: next.length, size: up.size });
  }
}

afterEach(() => vi.unstubAllGlobals());

const makeFile = (size: number) => {
  const data = new Uint8Array(size).map((_, i) => (i * 13 + 5) & 255);
  return { data, file: new File([data], 'big.xml.gz', { lastModified: 1234 }) };
};

describe('uploadSave', () => {
  it('uploads in chunks and completes', async () => {
    const server = new FakeSaveServer();
    server.install();
    const { data, file } = makeFile(2500);
    const phases: string[] = [];
    await uploadSave(file, { store: new MemStore(), chunkSize: 1000, onProgress: (p: UploadProgress) => phases.push(p.phase) });
    expect(server.puts).toEqual([
      { start: 0, end: 999 },
      { start: 1000, end: 1999 },
      { start: 2000, end: 2499 },
    ]);
    expect(server.completed).toBe(await shaHex(data));
    expect(phases).toContain('hashing');
    expect(phases.at(-1)).toBe('done');
  });

  it('resumes from the server offset after a simulated reload', async () => {
    const server = new FakeSaveServer();
    server.install();
    const store = new MemStore(); // localStorage survives the reload
    const { data, file } = makeFile(5000);

    // First page life: interrupted after two chunks.
    const ctl = new AbortController();
    server.stopAfterChunks = { n: 2, ctl };
    await expect(uploadSave(file, { store, chunkSize: 1000, signal: ctl.signal })).rejects.toThrow(/abort/i);
    expect(server.puts).toHaveLength(2);
    expect(store.m.size).toBe(1); // upload id remembered

    // "Reload": nothing in memory, the user picks the same file again.
    server.stopAfterChunks = null;
    server.calls.length = 0;
    server.puts.length = 0;
    const seen: UploadProgress[] = [];
    await uploadSave(makeFile(5000).file, { store, chunkSize: 1000, onProgress: (p) => seen.push(p) });

    expect(server.calls[0]).toBe('GET /api/v1/saves/uploads/u1'); // asked the server for its offset...
    expect(server.calls.filter((c) => c === 'POST /api/v1/saves/uploads')).toHaveLength(0); // ...and did not start over
    expect(server.puts[0]).toEqual({ start: 2000, end: 2999 }); // continued at the stored offset
    expect(server.puts).toHaveLength(3);
    expect(seen.some((p) => p.phase === 'hashing')).toBe(false); // the remembered hash is reused
    expect(seen.find((p) => p.phase === 'uploading')?.resumed).toBe(true);
    expect(server.completed).toBe(await shaHex(data));
    expect(store.m.size).toBe(0);
  });

  it('starts a new upload and resumes by hash when the server forgot the remembered id', async () => {
    const server = new FakeSaveServer();
    server.install();
    const store = new MemStore();
    const { file } = makeFile(1500);
    const ctl = new AbortController();
    server.stopAfterChunks = { n: 1, ctl };
    await expect(uploadSave(file, { store, chunkSize: 1000, signal: ctl.signal })).rejects.toThrow();
    server.uploads.clear(); // the id is gone but the part file stayed (the server resumes by hash)
    server.stopAfterChunks = null;
    server.puts.length = 0;
    await uploadSave(file, { store, chunkSize: 1000 });
    expect(server.puts).toEqual([{ start: 1000, end: 1499 }]);
  });

  it('recovers from a dropped chunk by asking the server for its offset', async () => {
    const server = new FakeSaveServer();
    server.install();
    const real = globalThis.fetch;
    let failed = false;
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string, init?: RequestInit) => {
        if (!failed && init?.method === 'PUT') {
          failed = true;
          return Promise.reject(new TypeError('network down'));
        }
        return real(url, init);
      }),
    );
    const { data, file } = makeFile(1500);
    await uploadSave(file, { store: new MemStore(), chunkSize: 1000 });
    expect(server.completed).toBe(await shaHex(data));
  });
});
