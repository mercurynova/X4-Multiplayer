import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/http';
import type { DashboardSnapshotDto, ServerInfoDto, SessionSummaryDto } from '../generated/generated';
import { E, groups, SNAPSHOT_EVENT } from '../hub/contract';
import { useHubGroup, useHubState } from '../hub/HubProvider';

// Pushes keep the header live; this poll is only a slow safety net.
const FALLBACK_REFRESH_MS = 60_000;

function formatUptime(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(h)}:${pad(m)}:${pad(s % 60)}`;
}

/** Server name and current session state for the header. State is text first, colour second. */
export function SessionStatus() {
  const [server, setServer] = useState<ServerInfoDto | null>(null);
  const [session, setSession] = useState<SessionSummaryDto | null | undefined>(undefined);
  const hubState = useHubState();

  const refresh = useCallback(() => {
    api
      .get<SessionSummaryDto | undefined>('/api/v1/sessions/current')
      .then((s) => setSession(s ?? null))
      .catch(() => undefined); // keep the last value; a 401 is handled globally
  }, []);

  useEffect(() => {
    api
      .get<ServerInfoDto>('/api/v1/server')
      .then(setServer)
      .catch(() => undefined);
  }, []);

  useEffect(() => {
    refresh();
    const t = setInterval(refresh, FALLBACK_REFRESH_MS);
    return () => clearInterval(t);
  }, [refresh]);

  // One REST fetch after each (re)connect covers anything missed while offline; the dashboard topic then pushes changes.
  useEffect(() => {
    if (hubState === 'connected') refresh();
  }, [hubState, refresh]);
  useHubGroup(groups.dashboard, (event, payload) => {
    if (event === E.SessionChanged) setSession(payload as SessionSummaryDto);
    else if (event === SNAPSHOT_EVENT) setSession((payload as DashboardSnapshotDto).session);
  });

  return (
    <div className="session-status">
      <span className="server-name">{server?.name ?? 'X4MP'}</span>
      {session === undefined ? null : session === null ? (
        <span className="muted">No session</span>
      ) : (
        <span>
          <span className="muted">Session</span> &ldquo;{session.name}&rdquo;{' '}
          <span className={`state state-${session.state.toLowerCase()}`}>{session.state.toUpperCase()}</span>{' '}
          {session.uptimeSeconds > 0 && <span className="muted">{formatUptime(session.uptimeSeconds)}</span>}
        </span>
      )}
    </div>
  );
}
