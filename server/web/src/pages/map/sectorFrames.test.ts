import { describe, expect, it } from 'vitest';
import type { SectorFrameDto } from '../../generated/generated';
import { Kind, SectorInterpolator, TELEPORT_METRES } from './sectorFrames';
import { SyntheticSector } from './syntheticFrames';

interface Ent {
  id: number;
  x: number;
  z: number;
  yaw?: number;
  cls?: number;
  player?: number | null;
}

const frame = (sectorId: number, tick: number, ents: Ent[]): SectorFrameDto => ({
  sectorId,
  tick,
  ids: ents.map((e) => e.id),
  x: ents.map((e) => e.x),
  z: ents.map((e) => e.z),
  yaw: ents.map((e) => e.yaw ?? 0),
  cls: ents.map((e) => e.cls ?? Kind.ShipM),
  flags: ents.map(() => 0),
  playerIds: ents.map((e) => e.player ?? null),
});

describe('SectorInterpolator', () => {
  it('starts an entity where it is and glides it to the newest frame over one interval', () => {
    const s = new SectorInterpolator();
    s.push(frame(5, 1, [{ id: 1, x: 0, z: 0 }]), 1000);
    s.sample(1000);
    expect(s.rx[0]).toBe(0);
    s.push(frame(5, 2, [{ id: 1, x: 100, z: -40 }]), 1250); // arrives 250 ms later
    s.sample(1250);
    expect(s.rx[0]).toBe(0); // t = 0: still at the previous position
    s.sample(1250 + 125);
    expect(s.rx[0]).toBeGreaterThan(40);
    expect(s.rx[0]).toBeLessThan(60);
    s.sample(1250 + 5000);
    expect(s.rx[0]).toBe(100); // clamped at the newest frame
    expect(s.rz[0]).toBe(-40);
  });

  it('matches entities by id, not by slot, when the order or membership changes', () => {
    const s = new SectorInterpolator();
    s.push(
      frame(5, 1, [
        { id: 1, x: 0, z: 0 },
        { id: 2, x: 500, z: 500 },
      ]),
      0,
    );
    s.push(
      frame(5, 2, [
        { id: 2, x: 520, z: 500 },
        { id: 3, x: 9, z: 9 },
        { id: 1, x: 10, z: 0 },
      ]),
      250,
    );
    s.sample(250);
    expect([s.rx[0], s.rz[0]]).toEqual([500, 500]); // id 2 came from 500
    expect([s.rx[1], s.rz[1]]).toEqual([9, 9]); // new entity: no previous, no glide
    expect([s.rx[2], s.rz[2]]).toEqual([0, 0]); // id 1 came from 0
    expect(s.indexOfId(3)).toBe(1);
    expect(s.indexOfId(77)).toBe(-1);
  });

  it('does not glide an entity across a teleport', () => {
    const s = new SectorInterpolator();
    s.push(frame(5, 1, [{ id: 1, x: 0, z: 0 }]), 0);
    s.push(frame(5, 2, [{ id: 1, x: TELEPORT_METRES + 10, z: 0 }]), 250);
    s.sample(250);
    expect(s.rx[0]).toBe(TELEPORT_METRES + 10);
  });

  it('turns the short way round for yaw', () => {
    const s = new SectorInterpolator();
    s.push(frame(5, 1, [{ id: 1, x: 0, z: 0, yaw: 350 }]), 0);
    s.push(frame(5, 2, [{ id: 1, x: 0, z: 0, yaw: 10 }]), 250);
    s.sample(250 + 125);
    const y = s.ryaw[0]!;
    expect(Math.abs(((y + 180) % 360) - 180)).toBeLessThan(15); // near 0/360, not near 180
  });

  it('restarts when the sector changes and finds a player by id', () => {
    const s = new SectorInterpolator();
    s.push(frame(5, 1, [{ id: 1, x: 0, z: 0, player: 9 }]), 0);
    expect(s.indexOfPlayer(9)).toBe(0);
    s.push(frame(6, 1, [{ id: 4, x: 3, z: 3 }]), 250);
    expect(s.sectorId).toBe(6);
    expect(s.n).toBe(1);
    expect(s.indexOfPlayer(9)).toBe(-1);
    s.sample(250);
    expect(s.rx[0]).toBe(3); // no glide from the other sector
  });

  it('groups entities by kind for batched drawing', () => {
    const s = new SectorInterpolator();
    s.push(
      frame(5, 1, [
        { id: 1, x: 0, z: 0, cls: Kind.Station },
        { id: 2, x: 0, z: 0, cls: Kind.ShipS },
        { id: 3, x: 0, z: 0, cls: Kind.Station },
      ]),
      0,
    );
    const range = (k: number) => Array.from(s.order.subarray(s.kindStart[k], s.kindStart[k + 1]));
    expect(range(Kind.Station)).toEqual([0, 2]);
    expect(range(Kind.ShipS)).toEqual([1]);
    expect(range(Kind.Gate)).toEqual([]);
  });

  it('handles 10,000 synthetic entities across many frames and keeps player entities', () => {
    const scene = new SyntheticSector(3, 10_000, 7, 4);
    const s = new SectorInterpolator();
    for (let i = 0; i < 5; i++) s.push(scene.next(), i * 250);
    expect(s.n).toBe(10_000);
    s.sample(5 * 250);
    expect(s.indexOfPlayer(1)).toBeGreaterThanOrEqual(0);
    expect(Number.isFinite(s.rx[9_999])).toBe(true);
  });
});
