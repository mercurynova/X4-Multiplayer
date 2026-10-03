import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import { BanDialog } from './ActionDialogs';
import { formatAgo, formatDuration, formatWhere } from './format';
import { PlayerActions } from './PlayerActions';
import { usePlayers } from './usePlayers';
import './players.css';

export function PlayersPage() {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { rows, error, refetch } = usePlayers();
  const [query, setQuery] = useState('');
  const [onlineOnly, setOnlineOnly] = useState(false);
  const [banOpen, setBanOpen] = useState(false);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (rows ?? [])
      .filter((r) => !onlineOnly || r.online)
      .filter(
        (r) =>
          q === '' ||
          r.player.name.toLowerCase().includes(q) ||
          (r.live?.remoteAddress ?? r.player.lastIp ?? '').toLowerCase().includes(q),
      )
      .sort((a, b) => Number(b.online) - Number(a.online) || a.player.name.localeCompare(b.player.name));
  }, [rows, query, onlineOnly]);

  return (
    <section className="players">
      <header className="page-head">
        <h1>Players</h1>
        <input
          type="search"
          className="search"
          placeholder="Search name or address"
          aria-label="Search players"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <div className="seg" role="group" aria-label="Filter">
          <button type="button" className={onlineOnly ? '' : 'ghost'} aria-pressed={onlineOnly} onClick={() => setOnlineOnly(true)}>
            Online
          </button>
          <button type="button" className={onlineOnly ? 'ghost' : ''} aria-pressed={!onlineOnly} onClick={() => setOnlineOnly(false)}>
            All
          </button>
        </div>
        {isAdmin && (
          <button type="button" className="ghost" onClick={() => setBanOpen(true)}>
            Ban IP or key…
          </button>
        )}
      </header>

      {error && <p role="alert">{error}</p>}
      {rows === null && !error && <p className="muted">Loading players…</p>}
      {rows !== null && shown.length === 0 && <p className="muted">No players match.</p>}

      {shown.length > 0 && (
        <div className="table-wrap">
          <table className="data">
            <caption className="visually-hidden">Players</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Status</th>
                <th scope="col">Team</th>
                <th scope="col">Role</th>
                <th scope="col">Sector</th>
                <th scope="col">FPS</th>
                <th scope="col">Address</th>
                <th scope="col">Playtime</th>
                <th scope="col">Last seen</th>
                <th scope="col">Flags</th>
                {isAdmin && <th scope="col">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {shown.map(({ player, live, online }) => (
                <tr key={player.id} data-player={player.name}>
                  <th scope="row">
                    <Link to={`/players/${player.id}`}>{player.name}</Link>
                  </th>
                  <td>
                    <span className={`presence ${online ? 'presence-on' : 'presence-off'}`}>
                      <span aria-hidden="true">{online ? '●' : '○'}</span> {online ? `Online${live ? ` ${Math.round(live.rttMs)} ms` : ''}` : 'Offline'}
                    </span>
                  </td>
                  <td>{live?.teamName ?? player.teamName ?? ''}</td>
                  <td>{online && live ? live.roles : ''}</td>
                  <td data-testid="player-sector">{online && live ? formatWhere(live) : ''}</td>
                  <td data-testid="player-fps">{online && live && live.stats ? Math.round(live.stats.fps) : ''}</td>
                  <td>{live?.remoteAddress ?? player.lastIp ?? ''}</td>
                  <td>{formatDuration(player.totalPlaytimeSeconds)}</td>
                  <td>{online ? 'now' : formatAgo(player.lastSeen)}</td>
                  <td>
                    {player.activeBan && <span className="flag flag-ban">BAN</span>}
                    {player.muted && <span className="flag flag-mute">MUTED</span>}
                  </td>
                  {isAdmin && (
                    <td>
                      <PlayerActions player={player} online={online} onChanged={() => void refetch()} />
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {banOpen && (
        <BanDialog
          onClose={() => setBanOpen(false)}
          onDone={(m) => {
            toast('success', m);
            void refetch();
          }}
        />
      )}
    </section>
  );
}
