import type { GalaxyDto, GalaxyPlayerDto } from '../../generated/generated';
import type { Bounds } from './camera';

/** Half the width of a sector in metres, used to place a player marker inside its sector hex (FakeNode sectors are about +-15 km). */
export const SECTOR_HALF_M = 20_000;

export interface LayoutSector {
  id: number;
  name: string;
  macro: string;
  clusterId: number;
  x: number;
  y: number;
  owner: string | null;
}

export interface LayoutLink {
  a: LayoutSector;
  b: LayoutSector;
  kind: string;
}

export interface GalaxyLayout {
  sectors: LayoutSector[];
  byId: Map<number, LayoutSector>;
  links: LayoutLink[];
  bounds: Bounds;
  /** Hex circumradius in world units (km): just under half the closest pair of sectors so neighbours do not overlap. */
  hexRadius: number;
}

/** Lays the galaxy out from the sector `mapPos` values and resolves the gate/highway links (links to unknown sectors are dropped). */
export function buildGalaxyLayout(g: GalaxyDto): GalaxyLayout {
  const sectors: LayoutSector[] = g.sectors.map((s) => ({
    id: s.id,
    name: s.name,
    macro: s.macro,
    clusterId: s.clusterId,
    x: s.mapPos.x,
    y: s.mapPos.y,
    owner: s.ownerFaction,
  }));
  const byId = new Map(sectors.map((s) => [s.id, s] as const));
  const links: LayoutLink[] = [];
  for (const l of g.links) {
    const a = byId.get(l.fromSector);
    const b = byId.get(l.toSector);
    if (a && b) links.push({ a, b, kind: l.kind });
  }
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const s of sectors) {
    minX = Math.min(minX, s.x);
    minY = Math.min(minY, s.y);
    maxX = Math.max(maxX, s.x);
    maxY = Math.max(maxY, s.y);
  }
  if (sectors.length === 0) {
    minX = minY = -1;
    maxX = maxY = 1;
  }
  return { sectors, byId, links, bounds: { minX, minY, maxX, maxY }, hexRadius: 0.48 * closestPair(sectors) };
}

function closestPair(sectors: LayoutSector[]): number {
  let best = Infinity;
  for (let i = 0; i < sectors.length; i++) {
    for (let j = i + 1; j < sectors.length; j++) {
      const d = Math.hypot(sectors[i]!.x - sectors[j]!.x, sectors[i]!.y - sectors[j]!.y);
      if (d > 1e-9 && d < best) best = d;
    }
  }
  return Number.isFinite(best) ? best : 1;
}

/** The six hex corners (pointy-top) as flat [x0,y0,x1,y1,...] around (cx, cy). */
export function hexCorners(cx: number, cy: number, r: number): number[] {
  const out: number[] = [];
  for (let i = 0; i < 6; i++) {
    const a = Math.PI / 6 + (i * Math.PI) / 3;
    out.push(cx + r * Math.cos(a), cy + r * Math.sin(a));
  }
  return out;
}

/** The sector whose hex contains the world point (nearest centre within the radius), or null. */
export function sectorAt(layout: GalaxyLayout, wx: number, wy: number): LayoutSector | null {
  let best: LayoutSector | null = null;
  let bestD = layout.hexRadius * layout.hexRadius;
  for (const s of layout.sectors) {
    const d = (s.x - wx) ** 2 + (s.y - wy) ** 2;
    if (d <= bestD) {
      best = s;
      bestD = d;
    }
  }
  return best;
}

/** Where a player at (px, pz) metres inside `sector` is drawn on the galaxy map: the sector centre plus the scaled offset, kept inside the hex. */
export function markerWorldPos(layout: GalaxyLayout, sector: LayoutSector, px: number, pz: number): [number, number] {
  const max = layout.hexRadius * 0.7;
  const k = max / SECTOR_HALF_M;
  let ox = px * k;
  let oy = pz * k;
  const len = Math.hypot(ox, oy);
  if (len > max) {
    ox *= max / len;
    oy *= max / len;
  }
  return [sector.x + ox, sector.y + oy];
}

/** Linear interpolation of a player marker between two galaxy frames; a sector change snaps to the newer position. */
export function interpolateMarker(
  layout: GalaxyLayout,
  prev: GalaxyPlayerDto | undefined,
  curr: GalaxyPlayerDto,
  t: number,
): [number, number] | null {
  const sector = layout.byId.get(curr.sectorId);
  if (!sector) return null;
  const [cx, cy] = markerWorldPos(layout, sector, curr.pos.x, curr.pos.z);
  if (!prev || prev.sectorId !== curr.sectorId) return [cx, cy];
  const [px, py] = markerWorldPos(layout, sector, prev.pos.x, prev.pos.z);
  const u = t < 0 ? 0 : t > 1 ? 1 : t;
  return [px + (cx - px) * u, py + (cy - py) * u];
}

/** A stable CSS colour for a faction/owner name (grey when unowned). */
export function ownerColor(owner: string | null, alpha = 1): string {
  if (!owner) return `hsla(215, 8%, 55%, ${alpha})`;
  return `hsla(${hash(owner) % 360}, 55%, 52%, ${alpha})`;
}

const PLAYER_HUES = [12, 48, 140, 190, 265, 320, 28, 95, 235, 350];

/** A stable CSS colour per player id (a fixed hue ladder so adjacent ids differ). */
export function playerColor(id: number, alpha = 1): string {
  return `hsla(${PLAYER_HUES[Math.abs(id) % PLAYER_HUES.length]}, 90%, 55%, ${alpha})`;
}

function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619);
  return h >>> 0;
}

export const TIER_ORDER = ['Near', 'Sector', 'Adjacent', 'Linger'] as const;
