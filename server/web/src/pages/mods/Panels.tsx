import { useState } from 'react';
import { Link } from 'react-router';
import type { ModCatalogEntryDto, PlayerModStatusDto, UnboundRejectionDto } from '../../generated/generated';
import type { Loaded } from './useMods';
import { formatTime, ModLinks, OutcomeBadge, ViolationLists, violationCount, violationSummary } from './common';
import { PlayerReportsDialog } from './PlayerReports';

const HIDDEN = 'Players’ mod lists are hidden from your role (ModListHidden). An admin can change who sees them with the ModListVisibility setting.';

function statusText(p: PlayerModStatusDto, policyVersion: number) {
  if (p.status === 'NoReport') return 'No report yet';
  if (p.status === 'Matches') return 'Matches';
  return p.reportPolicyVersion < policyVersion ? 'Differs (policy changed since the join)' : 'Differs';
}

/** Per-player status against the current policy, with the full violation lists and a link to the report history. */
export function PlayersPanel({ players, policyVersion, hidden }: { players: PlayerModStatusDto[]; policyVersion: number; hidden: boolean }) {
  const [open, setOpen] = useState<PlayerModStatusDto | null>(null);
  return (
    <div className="panel" aria-label="Per-player status">
      <h2>Players</h2>
      {hidden ? (
        <p role="status">{HIDDEN}</p>
      ) : players.length === 0 ? (
        <p className="muted">No player has reported mods yet.</p>
      ) : (
        <ul className="plain mod-players">
          {players.map((p) => (
            <li key={p.playerId}>
              <div className="row">
                <Link to={`/players/${p.playerId}`}>{p.name}</Link>
                {p.isAuthority && <span className="badge">authority</span>}
                {!p.online && <span className="badge">offline</span>}
                <OutcomeBadge outcome={p.outcome} />
                <span>{statusText(p, policyVersion)}</span>
                {p.reportedAt && <span className="muted">{formatTime(p.reportedAt)}</span>}
                <button type="button" className="ghost" aria-label={`Reports of ${p.name}`} onClick={() => setOpen(p)}>
                  Reports
                </button>
              </div>
              {violationCount(p.violation) > 0 && (
                <details>
                  <summary>{violationSummary(p.violation)}</summary>
                  <ViolationLists violation={p.violation} />
                </details>
              )}
            </li>
          ))}
        </ul>
      )}
      {open && <PlayerReportsDialog playerId={open.playerId} name={open.name} onClose={() => setOpen(null)} />}
    </div>
  );
}

/** Recent refusals: players whose latest report was rejected and connections whose key is not bound to a player yet. */
export function RejectionsPanel({
  players,
  rejections,
}: {
  players: PlayerModStatusDto[];
  rejections: Loaded<UnboundRejectionDto[]>;
}) {
  if (rejections.hidden) {
    return (
      <div className="panel" aria-label="Recent rejections">
        <h2>Recent rejections</h2>
        <p role="status">{HIDDEN}</p>
      </div>
    );
  }
  const rows = [
    ...players
      .filter((p) => p.outcome === 'Rejected' && p.reportedAt)
      .map((p) => ({ key: `p${p.playerId}`, name: p.name, at: p.reportedAt as string, policyVersion: p.reportPolicyVersion, extensions: p.extensionCount, violation: p.violation, note: 'player' })),
    ...(rejections.data ?? []).map((r) => ({
      key: `k${r.keyId}-${r.at}`,
      name: r.attemptedName || '(no name)',
      at: r.at,
      policyVersion: r.policyVersion,
      extensions: r.extensionCount,
      violation: r.violation,
      note: `unknown key ${r.keyId}`,
    })),
  ].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
  return (
    <div className="panel" aria-label="Recent rejections">
      <h2>Recent rejections</h2>
      {rejections.error && <p role="alert">{rejections.error}</p>}
      {rows.length === 0 ? (
        <p className="muted">Nobody has been refused for their mods.</p>
      ) : (
        <ul className="plain">
          {rows.map((r) => (
            <li key={r.key}>
              <div className="row">
                <strong>{r.name}</strong>
                <span className="muted">{r.note}</span>
                <span className="muted">{formatTime(r.at)}</span>
                <span className="badge">policy v{r.policyVersion}</span>
                <span className="muted">{r.extensions} extensions</span>
              </div>
              <ViolationLists violation={r.violation} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** What the server knows about every mod it has seen (names and Workshop ids from reports, links and notes from admins). */
export function CatalogPanel({
  catalog,
  canEdit,
  onEdit,
  onAdd,
}: {
  catalog: Loaded<ModCatalogEntryDto[]>;
  canEdit: boolean;
  onEdit: (e: ModCatalogEntryDto) => void;
  onAdd: (e: ModCatalogEntryDto) => void;
}) {
  return (
    <div className="panel" aria-label="Mod catalog">
      <h2>Catalog</h2>
      {catalog.hidden ? (
        <p role="status">{HIDDEN}</p>
      ) : catalog.error ? (
        <p role="alert">{catalog.error}</p>
      ) : !catalog.data ? (
        <p className="muted">Loading…</p>
      ) : catalog.data.length === 0 ? (
        <p className="muted">No mods seen yet.</p>
      ) : (
        <div className="table-scroll">
          <table className="data">
            <thead>
              <tr>
                <th scope="col">Mod</th>
                <th scope="col">Class override</th>
                <th scope="col">Links</th>
                <th scope="col">Notes</th>
                <th scope="col">In list</th>
                {canEdit && <th scope="col">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {catalog.data.map((c) => (
                <tr key={c.id}>
                  <td>
                    <strong>{c.name || c.id}</strong>
                    {c.isLibrary && <span className="badge">library</span>}
                    <div className="mono muted">{c.id}</div>
                  </td>
                  <td>{c.classOverride === 'Unknown' ? '' : c.classOverride}</td>
                  <td>
                    <ModLinks mod={c} />
                  </td>
                  <td>{c.notes}</td>
                  <td>{c.inPolicy ? 'yes' : 'no'}</td>
                  {canEdit && (
                    <td>
                      <div className="actions">
                        <button type="button" className="ghost" aria-label={`Edit ${c.name || c.id} in the catalog`} onClick={() => onEdit(c)}>
                          Edit
                        </button>
                        {!c.inPolicy && (
                          <button type="button" className="ghost" aria-label={`Add ${c.name || c.id} to the list`} onClick={() => onAdd(c)}>
                            Add to list
                          </button>
                        )}
                      </div>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
