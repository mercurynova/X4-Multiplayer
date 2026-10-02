import { useMemo, useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { ApiError } from '../../api/http';
import type { TeamDto, TeamRelationsDto } from '../../generated/generated';
import { teamsApi } from './teamsApi';
import { nextRelation, pairKey, relationOf, type Relation } from './teamsState';

const ICON: Record<Relation, string> = { Allied: '▲', Neutral: '○', Hostile: '✖' };

/**
 * The symmetric relation matrix (server-design 5.4a). A click cycles a cell Allied, Neutral, Hostile; edits are staged and
 * applied together as one `PUT /teams/relations`. Cells show a word and an icon, not colour alone.
 */
export function RelationGrid({ teams, relations, canEdit }: { teams: readonly TeamDto[]; relations: TeamRelationsDto; canEdit: boolean }) {
  const { toast } = useAlerts();
  const [staged, setStaged] = useState<ReadonlyMap<string, Relation>>(new Map());
  const [busy, setBusy] = useState(false);

  // Only edits that differ from what the server has count (a push that matches an edit makes it disappear).
  const changes = useMemo(() => {
    const list: { teamA: number; teamB: number; relation: Relation }[] = [];
    for (const [key, relation] of staged) {
      const [a, b] = key.split('-').map(Number) as [number, number];
      if (teams.some((t) => t.id === a) && teams.some((t) => t.id === b) && relationOf(relations, a, b) !== relation) {
        list.push({ teamA: a, teamB: b, relation });
      }
    }
    return list;
  }, [staged, teams, relations]);

  if (teams.length < 2) {
    return <p className="muted">Relations appear once there are two teams.</p>;
  }

  const shown = (a: number, b: number): Relation => staged.get(pairKey(a, b)) ?? relationOf(relations, a, b);
  const cycle = (a: number, b: number) =>
    setStaged((m) => {
      const next = new Map(m);
      next.set(pairKey(a, b), nextRelation(shown(a, b)));
      return next;
    });

  const apply = async () => {
    setBusy(true);
    try {
      await teamsApi.setRelations(changes);
      setStaged(new Map());
      toast('success', `Applied ${changes.length} relation${changes.length === 1 ? '' : 's'}.`);
    } catch (e) {
      toast('error', e instanceof ApiError ? e.message : 'Could not apply the relations.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="relation-grid">
      <div className="table-wrap">
        <table className="data">
          <caption className="visually-hidden">Relations between teams</caption>
          <thead>
            <tr>
              <td />
              {teams.map((t) => (
                <th key={t.id} scope="col">
                  {t.name}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {teams.map((row) => (
              <tr key={row.id}>
                <th scope="row">{row.name}</th>
                {teams.map((col) => {
                  if (row.id === col.id) return <td key={col.id} className="self" aria-label="same team">—</td>;
                  const r = shown(row.id, col.id);
                  const edited = changes.some((c) => pairKey(c.teamA, c.teamB) === pairKey(row.id, col.id));
                  const label = `${row.name} and ${col.name}: ${r}${edited ? ' (changed)' : ''}`;
                  return (
                    <td key={col.id}>
                      {canEdit ? (
                        <button
                          type="button"
                          className={`ghost rel rel-${r.toLowerCase()}${edited ? ' staged' : ''}`}
                          aria-label={label}
                          title={`Click to set ${nextRelation(r)}`}
                          onClick={() => cycle(row.id, col.id)}
                        >
                          {r.toUpperCase()} <span aria-hidden="true">{ICON[r]}</span>
                          {edited && <span aria-hidden="true">*</span>}
                        </button>
                      ) : (
                        <span className={`rel rel-${r.toLowerCase()}`} aria-label={label}>
                          {r.toUpperCase()} <span aria-hidden="true">{ICON[r]}</span>
                        </span>
                      )}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {canEdit && (
        <div className="row">
          <button type="button" className="ghost" disabled={changes.length === 0 || busy} onClick={() => setStaged(new Map())}>
            Discard
          </button>
          <button type="button" disabled={changes.length === 0 || busy} onClick={() => void apply()}>
            Apply relations ({changes.length})
          </button>
        </div>
      )}
    </div>
  );
}
