import { describe, expect, it } from 'vitest';
import type { GalaxyDto, GalaxyPlayerDto } from '../../generated/generated';
import { buildGalaxyLayout, hexCorners, interpolateMarker, markerWorldPos, ownerColor, playerColor, sectorAt, SECTOR_HALF_M } from './galaxyLayout';

const sector = (id: number, x: number, y: number, owner: string | null = null) => ({
  id,
  macro: `m${id}`,
  name: `S${id}`,
  clusterId: 1,
  mapPos: { x, y },
  ownerFaction: owner,
});

const galaxy: GalaxyDto = {
  saveSha256: 'x',
  clusters: [{ id: 1, macro: 'c' }],
  sectors: [sector(1, 0, 0, 'argon'), sector(2, 10, 0), sector(3, 0, 20)],
  links: [
    { fromSector: 1, toSector: 2, kind: 'Gate' },
    { fromSector: 2, toSector: 3, kind: 'Highway' },
    { fromSector: 3, toSector: 99, kind: 'Gate' }, // unknown target is dropped
  ],
};

const player = (sectorId: number, x: number, z: number): GalaxyPlayerDto => ({ id: 1, name: 'A', sectorId, pos: { x, y: 0, z }, headingDeg: 0 });

describe('galaxy layout', () => {
  const l = buildGalaxyLayout(galaxy);

  it('places sectors at their map positions and computes bounds', () => {
    expect(l.sectors.map((s) => [s.id, s.x, s.y])).toEqual([
      [1, 0, 0],
      [2, 10, 0],
      [3, 0, 20],
    ]);
    expect(l.bounds).toEqual({ minX: 0, minY: 0, maxX: 10, maxY: 20 });
  });
  it('keeps only links between known sectors', () => {
    expect(l.links.map((k) => [k.a.id, k.b.id, k.kind])).toEqual([
      [1, 2, 'Gate'],
      [2, 3, 'Highway'],
    ]);
  });
  it('sizes hexes under half the closest pair so neighbours do not overlap', () => {
    expect(l.hexRadius).toBeCloseTo(4.8, 6);
  });
  it('finds the sector under a world point', () => {
    expect(sectorAt(l, 1, 1)?.id).toBe(1);
    expect(sectorAt(l, 9, 0)?.id).toBe(2);
    expect(sectorAt(l, 5, 12)).toBeNull();
  });
  it('handles an empty galaxy', () => {
    const e = buildGalaxyLayout({ ...galaxy, sectors: [], links: [] });
    expect(e.sectors).toHaveLength(0);
    expect(Number.isFinite(e.hexRadius)).toBe(true);
  });
  it('produces six hex corners at the radius', () => {
    const c = hexCorners(5, 5, 2);
    expect(c).toHaveLength(12);
    for (let i = 0; i < 12; i += 2) expect(Math.hypot(c[i]! - 5, c[i + 1]! - 5)).toBeCloseTo(2, 9);
  });
  it('keeps markers inside the hex and scales the in-sector offset', () => {
    const s = l.byId.get(1)!;
    expect(markerWorldPos(l, s, 0, 0)).toEqual([0, 0]);
    const [x, y] = markerWorldPos(l, s, SECTOR_HALF_M * 10, 0); // far outside the nominal sector
    expect(Math.hypot(x - s.x, y - s.y)).toBeLessThanOrEqual(l.hexRadius * 0.7 + 1e-9);
  });
  it('interpolates a marker between frames and snaps after a sector change', () => {
    const a = player(1, 0, 0);
    const b = player(1, 10_000, 0);
    const [x0] = interpolateMarker(l, a, b, 0)!;
    const [x1] = interpolateMarker(l, a, b, 1)!;
    const [xh] = interpolateMarker(l, a, b, 0.5)!;
    expect(x0).toBeCloseTo(0, 9);
    expect(xh).toBeCloseTo((x0 + x1) / 2, 9);
    expect(x1).toBeGreaterThan(x0);
    expect(interpolateMarker(l, a, b, 7)![0]).toBeCloseTo(x1, 9); // t is clamped
    const jumped = interpolateMarker(l, player(1, 0, 0), player(2, 0, 0), 0)!;
    expect(jumped).toEqual([10, 0]); // snaps to the new sector centre at once
    expect(interpolateMarker(l, undefined, player(99, 0, 0), 0)).toBeNull();
  });
  it('gives stable colours per owner and per player', () => {
    expect(ownerColor('argon')).toBe(ownerColor('argon'));
    expect(ownerColor('argon')).not.toBe(ownerColor('paranid'));
    expect(playerColor(1)).not.toBe(playerColor(2));
  });
});
