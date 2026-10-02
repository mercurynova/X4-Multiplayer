import type { ReactNode } from 'react';
import type { PlayerLiveDto } from '../../generated/generated';
import './dashboard.css';
import { formatMs, formatNumber, formatRate, formatUptime } from './format';
import { Sparkline } from './Sparkline';
import { useDashboard } from './useDashboard';

const isAuthority = (p: PlayerLiveDto) => p.roles.toLowerCase().includes('authority');

function Tile({ title, children, testId }: { title: string; children: ReactNode; testId: string }) {
  return (
    <section className="tile" aria-label={title} data-testid={testId}>
      <h2>{title}</h2>
      {children}
    </section>
  );
}

export function Dashboard() {
  const { snapshot, players, alerts, history, loaded } = useDashboard();

  if (!loaded || !snapshot) {
    return (
      <section>
        <h1>Dashboard</h1>
        <p className="muted">Waiting for the server…</p>
      </section>
    );
  }

  const session = snapshot.session;
  const authority = players.find((p) => p.connected && isAuthority(p)) ?? null;
  const online = players.filter((p) => p.connected);
  const loading = online.filter((p) => !isAuthority(p) && p.phase !== 'InGame').length;
  const pingSeries = Object.values(history.ping).map((p) => ({ label: p.name, values: p.values }));

  return (
    <section className="dashboard">
      <h1>Dashboard</h1>
      <div className="tiles">
        <Tile title="Session" testId="tile-session">
          {session ? (
            <>
              <p className="big">
                <span className={`state state-${session.state.toLowerCase()}`}>{session.state.toUpperCase()}</span>
              </p>
              <p>{session.name}</p>
              <p className="muted">{session.saveName ?? 'no save'}</p>
              <p className="muted">up {formatUptime(session.uptimeSeconds)}</p>
            </>
          ) : (
            <p className="big muted">No session</p>
          )}
        </Tile>
        <Tile title="Players" testId="tile-players">
          <p className="big">
            {online.length} / {snapshot.maxPlayers}
          </p>
          <p className="muted">{loading} loading</p>
        </Tile>
        <Tile title="Authority" testId="tile-authority">
          {authority ? (
            <>
              <p className="big">{authority.fps > 0 ? `${Math.round(authority.fps)} FPS` : '- FPS'}</p>
              <p>{authority.name}</p>
              <p className="muted">{formatMs(authority.rttMs)} latency</p>
            </>
          ) : (
            <p className="big muted">None</p>
          )}
          <p className="muted">{formatNumber(snapshot.entitiesInMirror)} entities</p>
          <p className="muted">{formatNumber(snapshot.sectorsCaptured)} sectors captured</p>
        </Tile>
        <Tile title="Traffic" testId="tile-traffic">
          <p className="big">
            ▲ {formatRate(snapshot.traffic.kBpsOut)} ▼ {formatRate(snapshot.traffic.kBpsIn)}
          </p>
          <Sparkline
            label="Traffic, last 10 minutes"
            series={[
              { label: 'out', values: history.kBpsOut },
              { label: 'in', values: history.kBpsIn },
            ]}
          />
          <p className="muted">
            drops {snapshot.traffic.droppedRealtimeLast60s}/60s · tick p99 {snapshot.tickP99Ms.toFixed(1)} ms
          </p>
        </Tile>
        <Tile title="Alerts" testId="tile-alerts">
          <p className="big">{alerts.length}</p>
          {alerts.length === 0 ? (
            <p className="muted">All clear</p>
          ) : (
            <ul className="alert-list">
              {alerts.map((a) => (
                <li key={a.code} className={`sev-${a.severity.toLowerCase()}`}>
                  {a.text}
                </li>
              ))}
            </ul>
          )}
        </Tile>
      </div>

      <h2>Players</h2>
      <table className="players-table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Role</th>
            <th>State</th>
            <th>Ping</th>
            <th>FPS</th>
            <th>Address</th>
            <th>Connected</th>
          </tr>
        </thead>
        <tbody>
          {players.length === 0 && (
            <tr>
              <td colSpan={7} className="muted">
                No players
              </td>
            </tr>
          )}
          {players.map((p) => (
            <tr key={p.playerId} data-testid={`player-${p.playerId}`}>
              <td>{p.name}</td>
              <td>{isAuthority(p) ? 'AUTHORITY' : 'client'}</td>
              <td>{p.connected ? p.phase : 'Disconnected'}</td>
              <td>{p.connected ? formatMs(p.rttMs) : '-'}</td>
              <td>{p.fps > 0 ? Math.round(p.fps) : '-'}</td>
              <td className="muted">{p.remoteAddress ?? '-'}</td>
              <td className="muted">{p.connected ? formatUptime(p.connectedSeconds) : '-'}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <h2>Ping (ms, per player)</h2>
      {pingSeries.length === 0 ? <p className="muted">Collecting…</p> : <Sparkline label="Ping per player" series={pingSeries} height={80} />}
      <ul className="legend">
        {pingSeries.map((s) => (
          <li key={s.label}>{s.label}</li>
        ))}
      </ul>
    </section>
  );
}
