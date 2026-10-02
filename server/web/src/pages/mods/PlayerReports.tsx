import { useCallback, useEffect, useState } from 'react';
import { ApiError } from '../../api/http';
import type { ExtensionReportDto, PlayerExtensionsDto } from '../../generated/generated';
import { E, groups } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { Dialog } from '../players/Dialog';
import { formatTime, OutcomeBadge, ViolationLists, violationCount } from './common';
import { modsApi } from './modsApi';

export interface PlayerReportsState {
  data: PlayerExtensionsDto | null;
  hidden: boolean;
  error: string | null;
  loading: boolean;
}

/**
 * A player's latest extension report and its history. Reloads when the hub's mods topic says that player reported again
 * (the Player detail page and the Mods page's report dialog both use it).
 */
export function usePlayerReports(playerId: number): PlayerReportsState {
  const [state, setState] = useState<PlayerReportsState>({ data: null, hidden: false, error: null, loading: true });
  const load = useCallback(() => {
    modsApi
      .playerExtensions(playerId)
      .then((data) => setState({ data, hidden: false, error: null, loading: false }))
      .catch((e: unknown) => {
        if (e instanceof ApiError && e.status === 403 && e.code === 'ModListHidden') setState({ data: null, hidden: true, error: null, loading: false });
        else setState((cur) => ({ ...cur, loading: false, error: e instanceof Error ? e.message : 'Could not load the mod report.' }));
      });
  }, [playerId]);

  useEffect(() => {
    load();
  }, [load]);

  useHubGroup(groups.mods, (event, payload) => {
    if (event === E.PlayerModsReported && (payload as { playerId?: number } | null)?.playerId === playerId) load();
  });

  return state;
}

function ReportItems({ report }: { report: ExtensionReportDto }) {
  const items = report.items;
  return (
    <div className="table-scroll">
      <table className="data" aria-label="Reported mods">
        <thead>
          <tr>
            <th scope="col">Mod</th>
            <th scope="col">Version</th>
            <th scope="col">Source</th>
            <th scope="col">Class</th>
            <th scope="col">Enabled</th>
            <th scope="col">Flags</th>
          </tr>
        </thead>
        <tbody>
          {items.map((x) => (
            <tr key={x.id}>
              <td>
                {x.name || x.id} <span className="mono muted">{x.id}</span>
              </td>
              <td>{x.version}</td>
              <td>{x.source}</td>
              <td>{x.effectiveClass}</td>
              <td>{x.enabled ? 'yes' : 'no'}</td>
              <td>
                {x.hasNativeDll && <span className="badge">DLL</span>}
                {x.replacesBasegame && <span className="badge">replaces base</span>}
                {!x.inPolicy && <span className="badge">not in list</span>}
                {x.error && <span className="badge badge-bad">{x.error}</span>}
                {x.warning && <span className="badge badge-warn">{x.warning}</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** The report view: newest report in full (what the session said then and what the current policy says now), then the history. */
export function PlayerReportsView({ state }: { state: PlayerReportsState }) {
  if (state.hidden) return <p role="status">Players&apos; mod lists are hidden from your role (ModListHidden; see the ModListVisibility setting).</p>;
  if (state.error) return <p role="alert">{state.error}</p>;
  if (state.loading || !state.data) return <p className="muted">Loading mod report…</p>;
  const { latest, history } = state.data;
  if (!latest) return <p className="muted">This player has not reported any mods yet.</p>;
  return (
    <div className="player-mods">
      <p>
        Latest report {formatTime(latest.at)} <OutcomeBadge outcome={latest.outcome} /> against policy v{latest.policyVersion}, {latest.items.length} extensions (
        {latest.items.filter((x) => x.enabled).length} enabled).
      </p>
      {violationCount(latest.violation) > 0 && (
        <>
          <h3>What the session asked for then</h3>
          <ViolationLists violation={latest.violation} />
        </>
      )}
      {violationCount(latest.currentViolation) > 0 && (
        <>
          <h3>Against the current policy</h3>
          <ViolationLists violation={latest.currentViolation} />
        </>
      )}
      {violationCount(latest.violation) === 0 && violationCount(latest.currentViolation) === 0 && <p className="muted">Matches the session mod list.</p>}
      <details>
        <summary>All reported mods ({latest.items.length})</summary>
        <ReportItems report={latest} />
      </details>
      <h3>History</h3>
      <table className="data" aria-label="Report history">
        <thead>
          <tr>
            <th scope="col">When</th>
            <th scope="col">Outcome</th>
            <th scope="col">Policy</th>
            <th scope="col">Extensions</th>
            <th scope="col">Differences</th>
          </tr>
        </thead>
        <tbody>
          {history.map((h) => (
            <tr key={h.at}>
              <td>{formatTime(h.at)}</td>
              <td>
                <OutcomeBadge outcome={h.outcome} />
              </td>
              <td>v{h.policyVersion}</td>
              <td>
                {h.enabledCount}/{h.extensionCount} enabled
              </td>
              <td>{violationCount(h.violation) > 0 ? <ViolationLists violation={h.violation} /> : ''}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** The Mods section of the Player detail page. */
export function PlayerModsSection({ playerId }: { playerId: number }) {
  const state = usePlayerReports(playerId);
  return (
    <div className="panel" aria-label="Mods">
      <h2>Mods</h2>
      <PlayerReportsView state={state} />
    </div>
  );
}

export function PlayerReportsDialog({ playerId, name, onClose }: { playerId: number; name: string; onClose: () => void }) {
  const state = usePlayerReports(playerId);
  return (
    <Dialog title={`${name}: mod reports`} onClose={onClose}>
      <PlayerReportsView state={state} />
      <div className="dialog-buttons">
        <button type="button" onClick={onClose}>
          Close
        </button>
      </div>
    </Dialog>
  );
}
