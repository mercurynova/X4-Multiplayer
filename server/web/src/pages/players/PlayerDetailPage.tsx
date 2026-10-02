import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import { ApiError } from '../../api/http';
import type { DashboardSnapshotDto, PlayerDetailDto, PlayerLiveDto } from '../../generated/generated';
import { E, groups, SNAPSHOT_EVENT } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { problemToFormErrors } from '../../lib/problem';
import { ConfirmDialog } from './ActionDialogs';
import { formatDateTime, formatDuration } from './format';
import { PlayerModsSection } from '../mods/PlayerReports';
import { PlayerActions } from './PlayerActions';
import { playersApi } from './playersApi';
import './players.css';

const NOTES_MAX = 1000;

function Notes({ detail, canEdit, onSaved }: { detail: PlayerDetailDto; canEdit: boolean; onSaved: () => void }) {
  const { toast } = useAlerts();
  const saved = detail.player.notes ?? '';
  const [text, setText] = useState(saved);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Pick up a change made elsewhere while there is nothing unsaved here.
  const lastSaved = useRef(saved);
  useEffect(() => {
    if (saved !== lastSaved.current) {
      setText((t) => (t === lastSaved.current ? saved : t));
      lastSaved.current = saved;
    }
  }, [saved]);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    playersApi
      .setNotes(detail.player.id, text)
      .then(() => {
        lastSaved.current = text;
        toast('success', 'Notes saved.');
        onSaved();
      })
      .catch((err: unknown) => {
        const fe = problemToFormErrors(err);
        setError(fe.fields['notes'] ?? fe.form);
      })
      .finally(() => setBusy(false));
  };

  return (
    <form onSubmit={submit} className="panel">
      <h2>Notes</h2>
      <label>
        Admin notes
        <textarea
          rows={4}
          value={text}
          maxLength={NOTES_MAX}
          readOnly={!canEdit}
          onChange={(e) => setText(e.target.value)}
          aria-invalid={error ? true : undefined}
        />
      </label>
      {error && <p role="alert">{error}</p>}
      {canEdit && (
        <div>
          <button type="submit" disabled={busy || text === saved}>
            Save notes
          </button>
        </div>
      )}
    </form>
  );
}

function Connection({ live, address }: { live: PlayerLiveDto | null; address: string | null }) {
  if (!live || !live.connected) return <p className="muted">Not connected. Last address: {address ?? 'unknown'}.</p>;
  return (
    <dl className="facts">
      <dt>Role</dt>
      <dd>{live.roles}</dd>
      <dt>Phase</dt>
      <dd>{live.phase}</dd>
      <dt>Address</dt>
      <dd>{live.remoteAddress ?? ''}</dd>
      <dt>Ping</dt>
      <dd>{Math.round(live.rttMs)} ms</dd>
      <dt>FPS</dt>
      <dd>{Math.round(live.fps)}</dd>
      <dt>Connected for</dt>
      <dd>{formatDuration(live.connectedSeconds)}</dd>
    </dl>
  );
}

export function PlayerDetailPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const [detail, setDetail] = useState<PlayerDetailDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [live, setLive] = useState<PlayerLiveDto | null>(null);
  const [unban, setUnban] = useState<number | null>(null);

  const load = useCallback(
    () =>
      playersApi
        .get(id)
        .then((d) => {
          setDetail(d);
          setLive(d.live);
          setError(null);
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError && e.status === 404 ? 'No such player.' : e instanceof Error ? e.message : 'Could not load the player.'),
        ),
    [id],
  );

  useEffect(() => {
    void load();
  }, [load]);

  useHubGroup(groups.dashboard, (event, payload) => {
    if (event === SNAPSHOT_EVENT || event === E.Dashboard) {
      const mine = (payload as DashboardSnapshotDto).players?.find((p) => p.playerId === id) ?? null;
      setLive(mine);
    } else if (event === E.PlayerChanged) {
      const p = payload as PlayerLiveDto;
      if (p.playerId === id) setLive(p);
    } else if (event === E.PlayerRemoved) {
      if (Number(payload) === id) {
        setLive(null);
        void load();
      }
    }
  });

  if (error) {
    return (
      <section>
        <p>
          <Link to="/players">All players</Link>
        </p>
        <p role="alert">{error}</p>
      </section>
    );
  }
  if (!detail) return <p className="muted">Loading player…</p>;

  const player = detail.player;
  const online = live?.connected ?? false;

  return (
    <section className="player-detail">
      <p>
        <Link to="/players">All players</Link>
      </p>
      <header className="page-head">
        <h1>{player.name}</h1>
        <span className={`presence ${online ? 'presence-on' : 'presence-off'}`}>
          <span aria-hidden="true">{online ? '●' : '○'}</span> {online ? 'Online' : 'Offline'}
        </span>
        {player.activeBan && <span className="flag flag-ban">BAN</span>}
        {player.muted && <span className="flag flag-mute">MUTED</span>}
      </header>

      {isAdmin && (
        <PlayerActions player={player} online={online} onChanged={() => void load()} withRelease />
      )}

      <div className="grid-2">
        <div className="panel">
          <h2>Identity</h2>
          <dl className="facts">
            <dt>Player id</dt>
            <dd>{player.id}</dd>
            <dt>Key hash</dt>
            <dd className="mono">{detail.keyHash}</dd>
            <dt>First seen</dt>
            <dd>{formatDateTime(player.firstSeen)}</dd>
            <dt>Last seen</dt>
            <dd>{online ? 'now' : formatDateTime(player.lastSeen)}</dd>
            <dt>Total playtime</dt>
            <dd>{formatDuration(player.totalPlaytimeSeconds)}</dd>
            {player.muted && (
              <>
                <dt>Muted until</dt>
                <dd>{player.mutedUntil ? formatDateTime(player.mutedUntil) : 'lifted manually'}</dd>
              </>
            )}
          </dl>
        </div>
        <div className="panel">
          <h2>Connection</h2>
          <Connection live={live} address={player.lastIp} />
        </div>
      </div>

      <Notes detail={detail} canEdit={isAdmin} onSaved={() => void load()} />

      <PlayerModsSection playerId={player.id} />

      <div className="panel">
        <h2>Bans</h2>
        {detail.bans.length === 0 ? (
          <p className="muted">No bans.</p>
        ) : (
          <table className="data">
            <thead>
              <tr>
                <th scope="col">Created</th>
                <th scope="col">By</th>
                <th scope="col">Target</th>
                <th scope="col">Reason</th>
                <th scope="col">Expires</th>
                <th scope="col">State</th>
                {isAdmin && <th scope="col">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {detail.bans.map((b) => (
                <tr key={b.id}>
                  <td>{formatDateTime(b.createdAt)}</td>
                  <td>{b.createdBy}</td>
                  <td>{[b.playerName, b.ipCidr].filter(Boolean).join(' + ') || 'player'}</td>
                  <td>{b.reason}</td>
                  <td>{b.expiresAt ? formatDateTime(b.expiresAt) : 'never'}</td>
                  <td>{b.active ? 'Active' : 'Ended'}</td>
                  {isAdmin && (
                    <td>
                      {b.active && (
                        <button type="button" className="ghost" aria-label={`Revoke ban ${b.id}`} onClick={() => setUnban(b.id)}>
                          Revoke
                        </button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="panel">
        <h2>Sessions</h2>
        {detail.history.length === 0 ? (
          <p className="muted">No sessions yet.</p>
        ) : (
          <table className="data">
            <thead>
              <tr>
                <th scope="col">Session</th>
                <th scope="col">Role</th>
                <th scope="col">Joined</th>
                <th scope="col">Left</th>
                <th scope="col">Reason</th>
              </tr>
            </thead>
            <tbody>
              {detail.history.map((h, i) => (
                <tr key={`${h.sessionId}-${i}`}>
                  <td>{h.sessionName}</td>
                  <td>{h.role}</td>
                  <td>{formatDateTime(h.joinedAt)}</td>
                  <td>{h.leftAt ? formatDateTime(h.leftAt) : 'now'}</td>
                  <td>{h.leaveReason ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {unban !== null && (
        <ConfirmDialog
          title="Revoke ban"
          body="Revoke this ban? The target can connect again right away."
          confirm="Revoke ban"
          run={() => playersApi.unban(unban)}
          doneMessage="Ban revoked."
          onClose={() => setUnban(null)}
          onDone={(m) => {
            toast('success', m);
            void load();
          }}
        />
      )}
    </section>
  );
}
