import type { GalaxyFrameDto } from '../../generated/generated';
import { playerColor, TIER_ORDER } from './galaxyLayout';

export interface InterestRow {
  playerId: number;
  name: string;
  tier: string;
}

/** Which clients have this sector in which interest tier (Near, Sector, Adjacent, Linger), strongest tier first. */
export function interestFor(frame: GalaxyFrameDto | null, sectorId: number): InterestRow[] {
  if (!frame) return [];
  const names = new Map(frame.players.map((p) => [p.id, p.name] as const));
  const rows: InterestRow[] = [];
  for (const e of frame.interest) {
    if (e.sectorIds.includes(sectorId)) rows.push({ playerId: e.playerId, name: names.get(e.playerId) ?? `Player ${e.playerId}`, tier: e.tier });
  }
  const rank = (t: string) => {
    const i = (TIER_ORDER as readonly string[]).indexOf(t);
    return i < 0 ? 99 : i;
  };
  return rows.sort((a, b) => rank(a.tier) - rank(b.tier) || a.name.localeCompare(b.name));
}

export function InterestList({ frame, sectorId }: { frame: GalaxyFrameDto | null; sectorId: number }) {
  const rows = interestFor(frame, sectorId);
  return (
    <div data-testid="interest-list">
      <h2>Interest</h2>
      {rows.length === 0 ? (
        <p className="muted">No client has this sector in its interest set.</p>
      ) : (
        <ul>
          {rows.map((r) => (
            <li key={`${r.playerId}-${r.tier}`}>
              <span className="map-swatch" style={{ background: playerColor(r.playerId) }} />
              {r.name}
              <span className="map-tier">{r.tier}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
