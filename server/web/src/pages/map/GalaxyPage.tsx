import { useState } from 'react';
import { useNavigate } from 'react-router';
import { GalaxyCanvas, type GalaxyLayers } from './GalaxyCanvas';
import { ownerColor, playerColor } from './galaxyLayout';
import { sectorPath } from './follow';
import { InterestList } from './InterestList';
import './map.css';
import { useGalaxy } from './useGalaxy';

const LAYER_LABELS: [keyof GalaxyLayers, string][] = [
  ['players', 'Players'],
  ['interest', 'Interest'],
  ['gates', 'Gates'],
  ['heat', 'Ship heat'],
];

/** `/map`: the galaxy canvas with live player markers; clicking a sector opens its sector view. */
export function GalaxyPage() {
  const { layout, frames, frame } = useGalaxy();
  const navigate = useNavigate();
  const [layers, setLayers] = useState<GalaxyLayers>({ players: true, interest: true, gates: true, heat: false });
  const [hover, setHover] = useState<number | null>(null);

  const hovered = layout && hover !== null ? (layout.byId.get(hover) ?? null) : null;
  const agg = frame?.sectorAgg.find((a) => a.sectorId === hover);
  const playersHere = frame && hover !== null ? frame.players.filter((p) => p.sectorId === hover) : [];

  return (
    <section>
      <div className="map-head">
        <h1>Map</h1>
        <span className="crumbs">Galaxy</span>
        <div className="map-layers" role="group" aria-label="Layers">
          {LAYER_LABELS.map(([key, label]) => (
            <label key={key}>
              <input type="checkbox" checked={layers[key]} onChange={(e) => setLayers({ ...layers, [key]: e.target.checked })} />
              {label}
            </label>
          ))}
        </div>
      </div>
      {!layout ? (
        <p className="map-empty" data-testid="galaxy-waiting">
          Waiting for authority galaxy metadata. The map appears once the authority has joined the session.
        </p>
      ) : (
        <div className="map-layout">
          <div className="map-stage">
            <GalaxyCanvas
              layout={layout}
              frames={frames}
              layers={layers}
              hoverSector={hover}
              onHover={setHover}
              onOpen={(sectorId, follow) => void navigate(sectorPath(sectorId, follow))}
            />
            <p className="map-hint">wheel = zoom, drag = pan, click a sector (or a player) = open sector view</p>
          </div>
          <aside className="map-side" aria-label="Galaxy details">
            <div className="map-panel" data-testid="galaxy-selection">
              <h2>{hovered ? hovered.name : 'Sector'}</h2>
              {hovered ? (
                <>
                  <dl className="facts">
                    <dt>Owner</dt>
                    <dd>{hovered.owner ?? 'none'}</dd>
                    <dt>Ships</dt>
                    <dd>{agg?.ships ?? 0}</dd>
                    <dt>Stations</dt>
                    <dd>{agg?.stations ?? 0}</dd>
                    <dt>Players</dt>
                    <dd>{playersHere.length === 0 ? 'none' : playersHere.map((p) => p.name).join(', ')}</dd>
                  </dl>
                  <InterestList frame={frame} sectorId={hovered.id} />
                </>
              ) : (
                <p className="muted">Hover a sector for details.</p>
              )}
            </div>
            <div className="map-panel" data-testid="galaxy-players">
              <h2>Players ({frame?.players.length ?? 0})</h2>
              {frame && frame.players.length > 0 ? (
                <ul>
                  {frame.players.map((p) => (
                    <li key={p.id} data-testid={`galaxy-player-${p.name}`}>
                      <span className="map-swatch" style={{ background: playerColor(p.id) }} />
                      <button className="ghost" onClick={() => void navigate(sectorPath(p.sectorId, p.id))} aria-label={`Follow ${p.name}`}>
                        {p.name}
                      </button>
                      <span className="map-pos">
                        {layout.byId.get(p.sectorId)?.name ?? `sector ${p.sectorId}`} ({(p.pos.x / 1000).toFixed(1)}, {(p.pos.z / 1000).toFixed(1)} km)
                      </span>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="muted">No players in the universe yet.</p>
              )}
            </div>
            <div className="map-panel">
              <h2>Legend</h2>
              <div className="map-legend">
                <span>
                  <span className="map-swatch" style={{ background: ownerColor('x') }} /> owner colour
                </span>
                <span>thin line = gate</span>
                <span>thick line = highway</span>
                <span>ring = interest (solid Near/Sector, dashed Adjacent, dotted Linger)</span>
              </div>
            </div>
          </aside>
        </div>
      )}
    </section>
  );
}
