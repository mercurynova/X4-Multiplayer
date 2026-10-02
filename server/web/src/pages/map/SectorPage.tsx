import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import type { SectorFrameDto } from '../../generated/generated';
import { E, groups, ERROR_EVENT } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { parseFollow, resolveFollow, sectorPath } from './follow';
import { playerColor } from './galaxyLayout';
import { InterestList } from './InterestList';
import './map.css';
import { ALL_FILTERS, SectorCanvas, type Hovered, type SectorFilters, type SectorStats } from './SectorCanvas';
import { FLAG_DOCKED, FLAG_IN_HIGHWAY, kindName, SectorInterpolator } from './sectorFrames';
import { useGalaxy } from './useGalaxy';

const SHIP_LABELS = ['XS', 'S', 'M', 'L', 'XL'] as const;

/** `/map/:sectorId`: live top-down view of one sector (4 Hz frames, interpolated), with follow-a-player across jumps. */
export function SectorPage() {
  const params = useParams();
  const [search] = useSearchParams();
  const navigate = useNavigate();
  const sectorId = Number(params['sectorId']);
  const valid = Number.isInteger(sectorId) && sectorId > 0 && sectorId <= 65535;
  const follow = parseFollow(search.get('follow'));
  const { layout, frame } = useGalaxy();
  const [interp] = useState(() => new SectorInterpolator());
  const [info, setInfo] = useState({ n: 0, tick: 0 });
  const [error, setError] = useState<string | null>(null);
  const [filters, setFilters] = useState<SectorFilters>(ALL_FILTERS);
  const [stats, setStats] = useState<SectorStats | null>(null);
  const [hover, setHover] = useState<Hovered | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [trackNonce, setTrackNonce] = useState(0);
  const [detachedKey, setDetachedKey] = useState<string | null>(null);
  const trackKey = `${follow}:${sectorId}`;
  const tracking = detachedKey !== trackKey;

  useEffect(() => {
    interp.reset();
  }, [interp, sectorId]);

  useHubGroup(valid ? groups.sector(sectorId) : null, (event, payload) => {
    if (event === E.SectorFrame) {
      const f = payload as SectorFrameDto;
      if (f.sectorId !== sectorId) return; // a late frame of the sector we just left
      setError(null);
      interp.push(f, performance.now());
      setInfo({ n: f.ids.length, tick: f.tick });
    } else if (event === ERROR_EVENT) {
      setError(payload instanceof Error ? payload.message : String(payload));
    }
  });

  // Following a player across gate jumps: the 1 Hz galaxy frame says where they are now.
  const outcome = frame ? resolveFollow(follow, frame.players, sectorId) : null;
  const jumpTo = outcome?.kind === 'jump' ? outcome.sectorId : null;
  useEffect(() => {
    if (jumpTo !== null) void navigate(sectorPath(jumpTo, follow), { replace: true });
  }, [jumpTo, follow, navigate]);

  const playerNames = useMemo(() => new Map((frame?.players ?? []).map((p) => [p.id, p.name] as const)), [frame]);
  const playersHere = (frame?.players ?? []).filter((p) => p.sectorId === sectorId);
  const sector = layout?.byId.get(sectorId) ?? null;
  const followName = follow !== null ? (playerNames.get(follow) ?? `Player ${follow}`) : null;

  const hoverIdx = hover ? interp.indexOfId(hover.netId) : -1;
  const shownId = hover && hoverIdx >= 0 ? hover.netId : selected;
  const shownIdx = shownId !== null ? interp.indexOfId(shownId) : -1;
  const shown = shownIdx >= 0 ? describe(interp, shownIdx, playerNames) : null;

  const setShip = (i: number, v: boolean) => {
    const ships = [...filters.ships] as SectorFilters['ships'];
    ships[i] = v;
    setFilters({ ...filters, ships });
  };

  if (!valid) {
    return (
      <section>
        <h1>Sector map</h1>
        <p role="alert">Not a sector id: {params['sectorId']}</p>
        <Link to="/map">Back to the galaxy</Link>
      </section>
    );
  }

  return (
    <section>
      <div className="map-head">
        <h1>Sector view</h1>
        <span className="crumbs">
          <Link to="/map">Galaxy</Link> ▸ <span data-testid="sector-name">{sector ? sector.name : `Sector ${sectorId}`}</span>
        </span>
        {follow !== null && (
          <span data-testid="following">
            Following <strong>{followName}</strong>{' '}
            <button className="ghost" onClick={() => void navigate(sectorPath(sectorId, null))}>
              Stop
            </button>
          </span>
        )}
      </div>
      {error && (
        <p className="map-warn" role="alert" data-testid="sector-error">
          {error}
        </p>
      )}
      <div className="map-layout">
        <div className="map-stage">
          <SectorCanvas
            interp={interp}
            playerNames={playerNames}
            filters={filters}
            trackPlayerId={follow}
            trackNonce={trackNonce}
            selectedNetId={selected}
            onHover={setHover}
            onSelect={setSelected}
            onStats={setStats}
            onUserMove={() => setDetachedKey(trackKey)}
          />
          <p className="map-hint">wheel = zoom, drag = pan, click = select (top-down, X east / Z north)</p>
        </div>
        <aside className="map-side" aria-label="Sector details">
          <div className="map-panel">
            <h2>Entities</h2>
            <p data-testid="entity-count" data-count={info.n}>
              Entities: {info.n.toLocaleString()}
            </p>
            <p className="map-pos" data-testid="sector-stats">
              tick {info.tick}
              {stats ? ` · ${stats.fps.toFixed(0)} fps · draw ${stats.drawMs.toFixed(1)} ms` : ''}
            </p>
            {info.n === 0 && <p className="muted">Waiting for the authority to capture this sector (admin views join the capture set).</p>}
            {follow !== null && !tracking && (
              <button
                className="ghost"
                onClick={() => {
                  setDetachedKey(null);
                  setTrackNonce((n) => n + 1);
                }}
              >
                Centre on {followName}
              </button>
            )}
          </div>
          <div className="map-panel">
            <h2>Filters</h2>
            <div className="map-filters" role="group" aria-label="Filters">
              {SHIP_LABELS.map((l, i) => (
                <label key={l}>
                  <input type="checkbox" checked={filters.ships[i]} onChange={(e) => setShip(i, e.target.checked)} />
                  {l}
                </label>
              ))}
              <label>
                <input type="checkbox" checked={filters.stations} onChange={(e) => setFilters({ ...filters, stations: e.target.checked })} />
                Stations
              </label>
              <label>
                <input type="checkbox" checked={filters.gates} onChange={(e) => setFilters({ ...filters, gates: e.target.checked })} />
                Gates
              </label>
              <label>
                <input type="checkbox" checked={filters.other} onChange={(e) => setFilters({ ...filters, other: e.target.checked })} />
                Other
              </label>
              <label>
                <input type="checkbox" checked={filters.docked} onChange={(e) => setFilters({ ...filters, docked: e.target.checked })} />
                Docked
              </label>
            </div>
          </div>
          <div className="map-panel" data-testid="sector-players">
            <h2>Players here ({playersHere.length})</h2>
            {playersHere.length === 0 ? (
              <p className="muted">No player is in this sector.</p>
            ) : (
              <ul>
                {playersHere.map((p) => (
                  <li key={p.id}>
                    <span className="map-swatch" style={{ background: playerColor(p.id) }} />
                    {p.name}
                    {follow === p.id ? (
                      <span className="map-tier">following</span>
                    ) : (
                      <button className="ghost" aria-label={`Follow ${p.name}`} onClick={() => void navigate(sectorPath(sectorId, p.id))}>
                        Follow
                      </button>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </div>
          <div className="map-panel">
            <InterestList frame={frame} sectorId={sectorId} />
          </div>
          <div className="map-panel" data-testid="entity-details">
            <h2>{hover ? 'Hovered' : 'Selected'}</h2>
            {shown ? (
              <>
                <dl className="facts">
                  <dt>Id</dt>
                  <dd>{shown.id}</dd>
                  <dt>Kind</dt>
                  <dd>{shown.kind}</dd>
                  <dt>Pilot</dt>
                  <dd>{shown.player ?? 'AI / none'}</dd>
                  <dt>Position</dt>
                  <dd>
                    {shown.x.toLocaleString()}, {shown.z.toLocaleString()} m
                  </dd>
                  <dt>State</dt>
                  <dd>{shown.state}</dd>
                </dl>
                {shown.playerId !== null && shown.playerId !== follow && (
                  <button className="ghost" onClick={() => void navigate(sectorPath(sectorId, shown.playerId))}>
                    Follow this player
                  </button>
                )}
              </>
            ) : (
              <p className="muted">Hover or click an entity.</p>
            )}
          </div>
        </aside>
      </div>
    </section>
  );
}

function describe(interp: SectorInterpolator, i: number, names: ReadonlyMap<number, string>) {
  const pid = interp.playerIds[i] ?? null;
  const flags = interp.flags[i]!;
  const state = [flags & FLAG_DOCKED ? 'docked' : '', flags & FLAG_IN_HIGHWAY ? 'in highway' : ''].filter(Boolean).join(', ');
  return {
    id: interp.ids[i]!,
    kind: kindName(interp.cls[i]!),
    playerId: pid,
    player: pid === null ? null : names.get(pid) ?? `Player ${pid}`,
    x: Math.round(interp.curX[i]!),
    z: Math.round(interp.curZ[i]!),
    state: state || 'flying',
  };
}
