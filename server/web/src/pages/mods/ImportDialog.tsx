import { useEffect, useState } from 'react';
import { ApiError } from '../../api/http';
import type { ExtensionDto, ModPolicyDto } from '../../generated/generated';
import { Dialog } from '../players/Dialog';
import { modsApi } from './modsApi';

export interface ImportPreviewRow {
  id: string;
  name: string;
  rule: 'Required' | 'Allowed';
  /** `add` is new; `keep` is already in the list (a merge keeps the admin's links, notes and overrides). */
  action: 'add' | 'keep';
}

/**
 * What "Import from authority" will do (docs/mod-management.md 3.3): enabled DLC and sim mods become Required, enabled client-only mods
 * Allowed, disabled ones are not imported. A merge keeps existing entries; without a merge the list is replaced.
 */
export function previewImport(items: ExtensionDto[], policy: ModPolicyDto, merge: boolean): ImportPreviewRow[] {
  const present = new Set(policy.entries.map((e) => e.id));
  return items
    .filter((x) => x.enabled && !/^(x4native|x4mp)/i.test(x.id))
    .map((x) => {
      const clientOnly = x.effectiveClass === 'ClientOnly';
      return {
        id: x.id,
        name: x.name || x.id,
        rule: clientOnly ? ('Allowed' as const) : ('Required' as const),
        action: merge && present.has(x.id) ? ('keep' as const) : ('add' as const),
      };
    });
}

/** Shows what the authority's last report would put into the list and asks before changing anything. */
export function ImportDialog({ policy, onClose, onDone }: { policy: ModPolicyDto; onClose: () => void; onDone: (message: string) => void }) {
  const authorityId = policy.authorityPlayerId;
  const [items, setItems] = useState<ExtensionDto[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [merge, setMerge] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (authorityId === null) return;
    let alive = true;
    modsApi
      .playerExtensions(authorityId)
      .then((r) => alive && setItems(r.latest?.items ?? []))
      .catch((e: unknown) => alive && setLoadError(e instanceof ApiError ? e.message : 'Could not read the authority’s report.'));
    return () => {
      alive = false;
    };
  }, [authorityId]);

  const rows = items ? previewImport(items, policy, merge) : [];
  const removed = merge ? 0 : policy.entries.filter((e) => !rows.some((r) => r.id === e.id)).length;

  const run = () => {
    setBusy(true);
    setError(null);
    modsApi
      .importFromAuthority({ merge })
      .then(() => {
        onDone('Imported the authority’s mods.');
        onClose();
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not import.'))
      .finally(() => setBusy(false));
  };

  return (
    <Dialog title="Import from authority" onClose={onClose}>
      {authorityId === null ? (
        <p>The authority has not reported its mods yet. Connect the authority first.</p>
      ) : loadError ? (
        <p role="alert">{loadError}</p>
      ) : items === null ? (
        <p className="muted">Reading the authority&apos;s report…</p>
      ) : (
        <>
          <p>
            The authority&apos;s enabled DLC and sim mods become <strong>Required</strong>, its enabled client-only mods <strong>Allowed</strong>. Disabled ones
            are not imported.
          </p>
          <label className="check">
            <input type="checkbox" checked={merge} onChange={(e) => setMerge(e.target.checked)} /> Merge with the current list (keep my links, notes and
            overrides)
          </label>
          {rows.length === 0 ? (
            <p className="muted">Nothing to import.</p>
          ) : (
            <table className="data" aria-label="Import preview">
              <thead>
                <tr>
                  <th scope="col">Mod</th>
                  <th scope="col">Rule</th>
                  <th scope="col">Result</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.id}>
                    <td>{r.name}</td>
                    <td>{r.rule}</td>
                    <td>{r.action === 'add' ? 'Added' : 'Already in the list (version refreshed, the rest kept)'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {removed > 0 && <p className="field-error">{removed} entries not in the authority&apos;s report will be removed.</p>}
        </>
      )}
      {error && <p role="alert">{error}</p>}
      <div className="dialog-buttons">
        <button type="button" className="ghost" onClick={onClose}>
          Cancel
        </button>
        <button type="button" disabled={busy || authorityId === null || items === null || rows.length === 0} onClick={run}>
          Import {rows.filter((r) => r.action === 'add').length} mods
        </button>
      </div>
    </Dialog>
  );
}
