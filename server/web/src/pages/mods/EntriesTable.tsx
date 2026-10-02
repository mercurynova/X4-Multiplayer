import { useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { ApiError } from '../../api/http';
import type { ModEntryDto, ModPolicyDto } from '../../generated/generated';
import { ConfirmDialog } from '../players/ActionDialogs';
import { ModLinks } from './common';
import { modsApi, RULES, emptyEntryRequest } from './modsApi';

function versionText(e: ModEntryDto) {
  if (e.versionRule === 'Any') return 'any';
  return `${e.versionRule === 'Exact' ? '=' : '≥'}${e.version || '?'}`;
}

/** The session mod list. Editors get an on/off switch, a rule menu, Edit and Delete per row; viewers get the same table read-only. */
export function EntriesTable({
  policy,
  canEdit,
  playersHidden,
  onEdit,
  onChanged,
}: {
  policy: ModPolicyDto;
  canEdit: boolean;
  playersHidden: boolean;
  onEdit: (e: ModEntryDto) => void;
  onChanged: () => void;
}) {
  const { toast } = useAlerts();
  const [deleting, setDeleting] = useState<ModEntryDto | null>(null);

  const patch = (e: ModEntryDto, change: Partial<typeof emptyEntryRequest>) =>
    modsApi
      .putEntry(e.id, { ...emptyEntryRequest, ...change })
      .then(onChanged)
      .catch((err: unknown) => toast('error', err instanceof ApiError ? err.message : 'Could not save the change.'));

  if (policy.entries.length === 0) {
    return (
      <p className="muted">
        {policy.sourceMode === 'AuthorityDefines'
          ? 'The list is empty, so the authority’s own mods define the session. Import them to add links and notes, or add mods by hand.'
          : 'The list is empty. Import from the authority or add a mod.'}
      </p>
    );
  }

  return (
    <>
      <div className="table-scroll">
        <table className="data" aria-label="Session mod list">
          <thead>
            <tr>
              <th scope="col">On</th>
              <th scope="col">Mod</th>
              <th scope="col">Class</th>
              <th scope="col">Rule</th>
              <th scope="col">Version</th>
              <th scope="col">Links</th>
              {!playersHidden && <th scope="col">Players</th>}
              <th scope="col">Notes</th>
              {canEdit && <th scope="col">Actions</th>}
            </tr>
          </thead>
          <tbody>
            {policy.entries.map((e) => (
              <tr key={e.id}>
                <td>
                  <input
                    type="checkbox"
                    checked={e.enabled}
                    disabled={!canEdit}
                    aria-label={`Enabled: ${e.name}`}
                    onChange={(ev) => void patch(e, { enabled: ev.target.checked })}
                  />
                </td>
                <td>
                  <strong>{e.name}</strong>
                  {e.isLibrary && (
                    <span className="badge" title="An allowlisted library mod">
                      library
                    </span>
                  )}
                  <div className="mono muted">{e.id}</div>
                </td>
                <td>
                  {e.class}
                  {e.classOverride !== 'Unknown' && <span className="badge badge-warn" title="Set by an admin">override</span>}
                  {e.hasNativeDll && <span className="badge" title="Has a native DLL">DLL</span>}
                  {e.replacesBasegame && <span className="badge" title="Replaces base-game UI or files">replaces base</span>}
                  {e.saveDependent && <span className="badge" title="Marked as needed by saves">save</span>}
                </td>
                <td>
                  {canEdit ? (
                    <select value={e.rule} aria-label={`Rule: ${e.name}`} onChange={(ev) => void patch(e, { rule: ev.target.value })}>
                      {RULES.map((r) => (
                        <option key={r}>{r}</option>
                      ))}
                    </select>
                  ) : (
                    e.rule
                  )}
                </td>
                <td>{versionText(e)}</td>
                <td>
                  <ModLinks mod={e} />
                </td>
                {!playersHidden && (
                  <td>
                    {e.playersEnabled} on{e.playersDisabled > 0 && `, ${e.playersDisabled} off`}
                    {e.playersMissing > 0 && `, ${e.playersMissing} without`}
                  </td>
                )}
                <td>{e.notes}</td>
                {canEdit && (
                  <td>
                    <div className="actions">
                      <button type="button" className="ghost" aria-label={`Edit ${e.name}`} onClick={() => onEdit(e)}>
                        Edit
                      </button>
                      <button type="button" className="ghost" aria-label={`Delete ${e.name}`} onClick={() => setDeleting(e)}>
                        Delete
                      </button>
                    </div>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {deleting && (
        <ConfirmDialog
          title={`Delete ${deleting.name}`}
          body="Remove this mod from the session list? The server still remembers its links and notes in the catalog. The change applies to the next join."
          confirm="Delete"
          run={() => modsApi.deleteEntry(deleting.id)}
          doneMessage={`Removed ${deleting.name}.`}
          onClose={() => setDeleting(null)}
          onDone={(m) => {
            toast('success', m);
            onChanged();
          }}
        />
      )}
    </>
  );
}
