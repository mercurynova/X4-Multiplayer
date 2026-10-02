import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/http';
import type { ServerInfoDto, SessionDetailDto } from '../generated/generated';
import { useHubEvent, useHubState } from '../hub/HubProvider';

const REFRESH_MS = 15_000;

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
  const [session, setSession] = useState<SessionDetailDto | null | undefined>(undefined);
  const hubState = useHubState();

  const refresh = useCallback(() => {
    api
      .get<SessionDetailDto | undefined>('/api/v1/sessions/current')
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
    const t = setInterval(refresh, REFRESH_MS);
    return () => clearInterval(t);
  }, [refresh]);

  // Resync right after a (re)connect and on any pushed session change.
  useEffect(() => {
    if (hubState === 'connected') refresh();
  }, [hubState, refresh]);
  useHubEvent('SessionChanged', refresh);

  return (
    <div className="session-status">
      <span className="server-name">{server?.name ?? 'X4MP'}</span>
      {session === undefined ? null : session === null ? (
        <span className="muted">No session</span>
      ) : (
        <span>
          <span className="muted">Session</span> &ldquo;{session.name}&rdquo;{' '}
          <span className={`state state-${session.state.toLowerCase()}`}>{session.state.toUpperCase()}</span>{' '}
          {session.live && <span className="muted">{formatUptime(session.uptimeSeconds)}</span>}
        </span>
      )}
    </div>
  );
}
