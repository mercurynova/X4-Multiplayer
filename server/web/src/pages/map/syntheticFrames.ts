import type { SectorFrameDto } from '../../generated/generated';
import { Kind } from './sectorFrames';

/**
 * Deterministic synthetic sector frames for the performance harness and tests: ships drift and bounce inside a +-15 km
 * box, a few stations and gates stand still, and a handful of players are included. Positions are rounded to 10 m like the
 * real hub frames.
 */
export class SyntheticSector {
  private readonly n: number;
  private readonly x: Float64Array;
  private readonly z: Float64Array;
  private readonly vx: Float64Array;
  private readonly vz: Float64Array;
  private readonly cls: number[];
  private readonly player: (number | null)[];
  private seed: number;
  private tick = 0;

  constructor(
    readonly sectorId: number,
    count: number,
    seed = 1,
    players = 5,
  ) {
    this.n = count;
    this.seed = seed;
    this.x = new Float64Array(count);
    this.z = new Float64Array(count);
    this.vx = new Float64Array(count);
    this.vz = new Float64Array(count);
    this.cls = new Array<number>(count);
    this.player = new Array<number | null>(count).fill(null);
    for (let i = 0; i < count; i++) {
      this.x[i] = (this.rand() * 2 - 1) * 15_000;
      this.z[i] = (this.rand() * 2 - 1) * 15_000;
      const r = this.rand();
      const stationShare = 0.02;
      if (i < 4) this.cls[i] = Kind.Gate;
      else if (r < stationShare) this.cls[i] = Kind.Station;
      else {
        this.cls[i] = Kind.ShipXS + Math.min(4, Math.floor(this.rand() ** 2 * 5));
        const speed = 40 + this.rand() * 200;
        const a = this.rand() * Math.PI * 2;
        this.vx[i] = Math.cos(a) * speed;
        this.vz[i] = Math.sin(a) * speed;
      }
    }
    for (let p = 0; p < Math.min(players, count - 4); p++) {
      const i = 4 + p;
      this.cls[i] = Kind.ShipM;
      this.player[i] = p + 1;
      const speed = 250;
      this.vx[i] = speed;
      this.vz[i] = 0;
    }
  }

  private rand(): number {
    // LCG (numerical recipes), enough for a synthetic scene
    this.seed = (Math.imul(this.seed, 1664525) + 1013904223) >>> 0;
    return this.seed / 4294967296;
  }

  /** Advances the scene by `dtSeconds` and returns the frame. */
  next(dtSeconds = 0.25): SectorFrameDto {
    this.tick++;
    const ids: number[] = new Array<number>(this.n);
    const xs: number[] = new Array<number>(this.n);
    const zs: number[] = new Array<number>(this.n);
    const yaws: number[] = new Array<number>(this.n);
    const flags: number[] = new Array<number>(this.n);
    for (let i = 0; i < this.n; i++) {
      let x = this.x[i]! + this.vx[i]! * dtSeconds;
      let z = this.z[i]! + this.vz[i]! * dtSeconds;
      if (x > 15_000 || x < -15_000) {
        this.vx[i] = -this.vx[i]!;
        x = Math.max(-15_000, Math.min(15_000, x));
      }
      if (z > 15_000 || z < -15_000) {
        this.vz[i] = -this.vz[i]!;
        z = Math.max(-15_000, Math.min(15_000, z));
      }
      this.x[i] = x;
      this.z[i] = z;
      ids[i] = 1000 + i;
      xs[i] = Math.round(x / 10) * 10;
      zs[i] = Math.round(z / 10) * 10;
      const deg = (Math.atan2(this.vx[i]!, this.vz[i]!) * 180) / Math.PI;
      yaws[i] = Math.round(((deg % 360) + 360) % 360);
      flags[i] = 0;
    }
    return { sectorId: this.sectorId, tick: this.tick, ids, x: xs, z: zs, yaw: yaws, cls: this.cls, flags, playerIds: this.player };
  }
}
