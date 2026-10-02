import { useRef, useState, type DragEvent } from 'react';
import { api } from '../../api/http';
import { useAlerts } from '../../alerts/AlertsProvider';
import type { SaveDto } from '../../generated/generated';
import { problemToFormErrors } from '../../lib/problem';
import { ConfirmDialog } from './ConfirmDialog';
import { formatBytes, formatDateTime, percent } from './format';
import { uploadSave, type UploadProgress } from './uploader';

interface UploadState {
  fileName: string;
  progress: UploadProgress | null;
  error: string | null;
}

function uploadText(u: UploadState): string {
  const p = u.progress;
  if (!p) return 'Starting...';
  if (p.phase === 'hashing') return `Checking ${percent(p.done, p.total)}% (SHA-256)`;
  if (p.phase === 'finalizing') return 'Verifying on the server...';
  if (p.phase === 'done') return 'Done, SHA-256 verified.';
  return `${p.resumed ? 'Resumed: ' : ''}${percent(p.done, p.total)}% (${formatBytes(p.done)} of ${formatBytes(p.total)})`;
}

/** The saves library: list, resumable upload, download, rename/pin, delete and "use for the next session". */
export function SavesLibrary({
  saves,
  isAdmin,
  selectedSha,
  onSelect,
  onChanged,
}: {
  saves: readonly SaveDto[];
  isAdmin: boolean;
  selectedSha: string | null;
  onSelect: (sha: string | null) => void;
  onChanged: () => void;
}) {
  const { toast } = useAlerts();
  const [upload, setUpload] = useState<UploadState | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<SaveDto | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [renaming, setRenaming] = useState<{ sha: string; name: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [dragOver, setDragOver] = useState(false);

  async function startUpload(file: File) {
    abortRef.current?.abort();
    const ctl = new AbortController();
    abortRef.current = ctl;
    setUpload({ fileName: file.name, progress: null, error: null });
    try {
      const saved = await uploadSave(file, {
        signal: ctl.signal,
        onProgress: (progress) => setUpload((u) => (u ? { ...u, progress } : u)),
      });
      toast('success', `Uploaded ${saved.displayName}.`);
      setUpload((u) => (u ? { ...u, progress: { phase: 'done', done: file.size, total: file.size, resumed: u.progress?.resumed ?? false } } : u));
      onChanged();
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') {
        setUpload((u) => (u ? { ...u, error: 'Paused. Choose the same file again to resume.' } : u));
      } else {
        setUpload((u) => (u ? { ...u, error: problemToFormErrors(e).form } : u));
      }
    }
  }

  function pick(files: FileList | null) {
    const f = files?.[0];
    if (f) void startUpload(f);
  }

  function onDrop(e: DragEvent) {
    e.preventDefault();
    setDragOver(false);
    pick(e.dataTransfer.files);
  }

  async function run(action: () => Promise<unknown>, errorTarget: 'toast' | 'delete' = 'toast') {
    setBusy(true);
    try {
      await action();
      onChanged();
      return true;
    } catch (e) {
      const msg = problemToFormErrors(e).form;
      if (errorTarget === 'delete') setDeleteError(msg);
      else toast('error', msg);
      return false;
    } finally {
      setBusy(false);
    }
  }

  const uploading = upload?.progress && upload.progress.phase !== 'done' && !upload.error;

  return (
    <section aria-labelledby="saves-h">
      <h2 id="saves-h">Saves</h2>
      {isAdmin && (
        <div
          className={`dropzone${dragOver ? ' dropzone-over' : ''}`}
          onDragOver={(e) => {
            e.preventDefault();
            setDragOver(true);
          }}
          onDragLeave={() => setDragOver(false)}
          onDrop={onDrop}
        >
          <label>
            Upload a save (.xml.gz) or drop it here
            <input type="file" accept=".gz,.xml.gz,application/gzip" onChange={(e) => pick(e.target.files)} />
          </label>
          {upload && (
            <div role="status" aria-label="Upload progress">
              <span>{upload.fileName}: </span>
              <span>{upload.error ?? uploadText(upload)}</span>
              {upload.progress && upload.progress.phase !== 'done' && !upload.error && (
                <progress value={upload.progress.done} max={Math.max(upload.progress.total, 1)} aria-label="Upload progress bar" />
              )}
              {uploading && (
                <button type="button" className="ghost" onClick={() => abortRef.current?.abort()}>
                  Pause
                </button>
              )}
            </div>
          )}
          {upload?.error && !upload.error.startsWith('Paused') && (
            <p className="field-error" role="alert">
              {upload.error}
            </p>
          )}
        </div>
      )}

      {deleteError && (
        <p className="field-error" role="alert">
          {deleteError}
        </p>
      )}
      {confirmDelete && (
        <ConfirmDialog
          title={`Delete ${confirmDelete.displayName}?`}
          confirmLabel="Delete save"
          danger
          busy={busy}
          onCancel={() => setConfirmDelete(null)}
          onConfirm={() => {
            const target = confirmDelete;
            setDeleteError(null);
            void run(() => api.delete(`/api/v1/saves/${target.sha256}`), 'delete').then(() => {
              setConfirmDelete(null);
              if (selectedSha === target.sha256) onSelect(null);
            });
          }}
        >
          <p>The file is removed from the server. Saves used by a session cannot be deleted.</p>
        </ConfirmDialog>
      )}

      {saves.length === 0 ? (
        <p className="muted">No saves yet. Upload one, or let the authority upload during a session.</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Size</th>
                <th>Source</th>
                <th>Game ver.</th>
                <th>Saved at</th>
                <th>SHA-256</th>
                <th>Status</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {saves.map((s) => (
                <tr key={s.sha256}>
                  <td>
                    {renaming?.sha === s.sha256 ? (
                      <form
                        onSubmit={(e) => {
                          e.preventDefault();
                          void run(() => api.patch(`/api/v1/saves/${s.sha256}`, { displayName: renaming.name })).then((ok) => {
                            if (ok) setRenaming(null);
                          });
                        }}
                      >
                        <input
                          aria-label="New name"
                          value={renaming.name}
                          maxLength={128}
                          onChange={(e) => setRenaming({ sha: s.sha256, name: e.target.value })}
                        />
                        <button type="submit" disabled={busy || renaming.name.trim() === ''}>
                          Save name
                        </button>
                        <button type="button" className="ghost" onClick={() => setRenaming(null)}>
                          Cancel
                        </button>
                      </form>
                    ) : (
                      <>
                        {s.displayName} {s.pinned && <span className="badge">pinned</span>}
                      </>
                    )}
                  </td>
                  <td>{formatBytes(s.sizeBytes)}</td>
                  <td>{s.source}</td>
                  <td>{s.gameVersion ?? '-'}</td>
                  <td>{formatDateTime(s.saveTime ?? s.uploadedAt)}</td>
                  <td title={s.sha256}>
                    <code>{s.sha256.slice(0, 12)}</code>
                  </td>
                  <td>
                    {s.current && <span className="badge">in use</span>} {selectedSha === s.sha256 && <span className="badge">next session</span>}{' '}
                    {!s.ghostsCleaned && <span className="badge badge-warn">ghosts not cleaned</span>}
                  </td>
                  <td className="actions">
                    <a href={`/api/v1/saves/${s.sha256}/download`} download aria-label={`Download ${s.displayName}`}>
                      Download
                    </a>
                    {isAdmin && (
                      <>
                        <button
                          type="button"
                          className="ghost"
                          aria-label={`Use ${s.displayName} for the next session`}
                          aria-pressed={selectedSha === s.sha256}
                          onClick={() => onSelect(selectedSha === s.sha256 ? null : s.sha256)}
                        >
                          {selectedSha === s.sha256 ? 'Selected' : 'Select'}
                        </button>
                        <button
                          type="button"
                          className="ghost"
                          aria-label={`Rename ${s.displayName}`}
                          onClick={() => setRenaming({ sha: s.sha256, name: s.displayName })}
                        >
                          Rename
                        </button>
                        <button
                          type="button"
                          className="ghost"
                          aria-label={`${s.pinned ? 'Unpin' : 'Pin'} ${s.displayName}`}
                          onClick={() => void run(() => api.patch(`/api/v1/saves/${s.sha256}`, { pinned: !s.pinned }))}
                        >
                          {s.pinned ? 'Unpin' : 'Pin'}
                        </button>
                        <button
                          type="button"
                          className="ghost"
                          aria-label={`Delete ${s.displayName}`}
                          onClick={() => {
                            setDeleteError(null);
                            setConfirmDelete(s);
                          }}
                        >
                          Delete
                        </button>
                      </>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
