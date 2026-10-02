import type { SectorFrameDto } from '../../generated/generated';

/** `EntityKind` numbers in the sector frame's `cls` array (protocol/schema/common.fbs). */
export const Kind = {
  Unknown: 0,
  ShipXS: 1,
  ShipS: 2,
  ShipM: 3,
  ShipL: 4,
  ShipXL: 5,
  Station: 6,
  Gate: 7,
  Accelerator: 8,
  HighwayEntry: 9,
  Satellite: 10,
  NavBeacon: 11,
  ResourceProbe: 12,
  Mine: 13,
  LaserTower: 14,
  Drone: 15,
  Lockbox: 16,
  Crate: 17,
  Other: 18,
} as const;
export const KIND_COUNT = 19;

const KIND_NAMES = [
  'Unknown', 'Ship XS', 'Ship S', 'Ship M', 'Ship L', 'Ship XL', 'Station', 'Gate', 'Accelerator', 'Highway entry',
  'Satellite', 'Nav beacon', 'Resource probe', 'Mine', 'Laser tower', 'Drone', 'Lockbox', 'Crate', 'Other',
];

export function kindName(cls: number): string {
  return KIND_NAMES[cls] ?? `Kind ${cls}`;
}

export const isShip = (cls: number) => cls >= Kind.ShipXS && cls <= Kind.ShipXL;

/** `StateFlags` bit for a docked entity (position meaningless). */
export const FLAG_DOCKED = 1 << 1;
export const FLAG_IN_HIGHWAY = 1 << 2;

/** A jump larger than this many metres between two frames is a teleport (gate, spawn), so the entity is not interpolated. */
export const TELEPORT_METRES = 2_500;

const DEFAULT_INTERVAL_MS = 250;

/**
 * The latest two sector frames of one sector and the smoothing between them (server-design 5.5: linear interpolation
 * between the last two frames). Rendering runs one frame behind: at `t = 0` an entity is where the previous frame had it, at
 * `t = 1` where the newest has it, with `t` advancing over the measured gap between arrivals. Nothing is allocated per
 * sample; the arrays grow only when a frame has more entities than any before.
 */
export class SectorInterpolator {
  sectorId = 0;
  tick = 0;
  n = 0;
  ids = new Float64Array(0);
  cls = new Uint8Array(0);
  flags = new Uint16Array(0);
  playerIds: (number | null)[] = [];
  curX = new Float32Array(0);
  curZ = new Float32Array(0);
  curYaw = new Float32Array(0);
  prevX = new Float32Array(0);
  prevZ = new Float32Array(0);
  prevYaw = new Float32Array(0);
  /** Output of `sample`. */
  rx = new Float32Array(0);
  rz = new Float32Array(0);
  ryaw = new Float32Array(0);
  /** Entity indices sorted by kind; `kindStart[k]..kindStart[k+1]` is kind `k`. */
  order = new Int32Array(0);
  kindStart = new Int32Array(KIND_COUNT + 1);
  /** Frames received for the current sector. */
  frames = 0;
  private index = new Map<number, number>();
  private arrivedAt = 0;
  private intervalMs = DEFAULT_INTERVAL_MS;

  reset(): void {
    this.n = 0;
    this.frames = 0;
    this.index.clear();
    this.sectorId = 0;
    this.intervalMs = DEFAULT_INTERVAL_MS;
    this.kindStart.fill(0);
    this.playerIds = [];
  }

  /** Adds a received frame. A frame for another sector than the current one restarts the buffer. */
  push(f: SectorFrameDto, nowMs: number): void {
    if (f.sectorId !== this.sectorId) {
      this.reset();
      this.sectorId = f.sectorId;
    }
    const n = f.ids.length;
    if (this.frames > 0) {
      const gap = nowMs - this.arrivedAt;
      if (gap > 20) this.intervalMs = Math.min(1500, Math.max(80, this.intervalMs * 0.6 + gap * 0.4));
    }
    this.grow(n);

    // The old "current" becomes "previous": copy it per matching id into prev* aligned to the new ordering.
    const oldIndex = this.index;
    const oldX = this.curX.slice(0, this.n);
    const oldZ = this.curZ.slice(0, this.n);
    const oldYaw = this.curYaw.slice(0, this.n);
    const newIndex = new Map<number, number>();
    for (let i = 0; i < n; i++) {
      const id = f.ids[i]!;
      newIndex.set(id, i);
      const x = f.x[i]!;
      const z = f.z[i]!;
      const yaw = f.yaw[i]!;
      this.ids[i] = id;
      this.cls[i] = f.cls[i]!;
      this.flags[i] = f.flags[i]!;
      this.curX[i] = x;
      this.curZ[i] = z;
      this.curYaw[i] = yaw;
      const j = oldIndex.get(id);
      if (j !== undefined && this.frames > 0 && Math.abs(oldX[j]! - x) + Math.abs(oldZ[j]! - z) <= TELEPORT_METRES) {
        this.prevX[i] = oldX[j]!;
        this.prevZ[i] = oldZ[j]!;
        this.prevYaw[i] = oldYaw[j]!;
      } else {
        this.prevX[i] = x;
        this.prevZ[i] = z;
        this.prevYaw[i] = yaw;
      }
    }
    this.playerIds = f.playerIds;
    this.n = n;
    this.index = newIndex;
    this.tick = f.tick;
    this.arrivedAt = nowMs;
    this.frames++;
    this.sortByKind();
  }

  /** The interpolation parameter at `nowMs` (0 at arrival of the newest frame, 1 one interval later). */
  progress(nowMs: number): number {
    const t = (nowMs - this.arrivedAt) / this.intervalMs;
    return t < 0 ? 0 : t > 1 ? 1 : t;
  }

  /** Fills `rx`, `rz`, `ryaw` for the first `n` entities. */
  sample(nowMs: number): void {
    const t = this.progress(nowMs);
    const { n, prevX, prevZ, curX, curZ, prevYaw, curYaw, rx, rz, ryaw } = this;
    for (let i = 0; i < n; i++) {
      rx[i] = prevX[i]! + (curX[i]! - prevX[i]!) * t;
      rz[i] = prevZ[i]! + (curZ[i]! - prevZ[i]!) * t;
      let dy = curYaw[i]! - prevYaw[i]!;
      if (dy > 180) dy -= 360;
      else if (dy < -180) dy += 360;
      ryaw[i] = prevYaw[i]! + dy * t;
    }
  }

  /** Index of the entity with this net id in the newest frame, or -1. */
  indexOfId(id: number): number {
    return this.index.get(id) ?? -1;
  }

  /** Index of the entity piloted by this player in the newest frame, or -1. */
  indexOfPlayer(playerId: number): number {
    for (let i = 0; i < this.n; i++) if (this.playerIds[i] === playerId) return i;
    return -1;
  }

  private grow(n: number): void {
    if (n <= this.ids.length) return;
    const cap = Math.max(n, Math.ceil(this.ids.length * 1.5), 64);
    const keep = this.n;
    const f32 = (a: Float32Array) => {
      const b = new Float32Array(cap);
      b.set(a.subarray(0, keep));
      return b;
    };
    this.ids = new Float64Array(cap);
    this.cls = new Uint8Array(cap);
    this.flags = new Uint16Array(cap);
    this.curX = f32(this.curX);
    this.curZ = f32(this.curZ);
    this.curYaw = f32(this.curYaw);
    this.prevX = new Float32Array(cap);
    this.prevZ = new Float32Array(cap);
    this.prevYaw = new Float32Array(cap);
    this.rx = new Float32Array(cap);
    this.rz = new Float32Array(cap);
    this.ryaw = new Float32Array(cap);
    this.order = new Int32Array(cap);
  }

  private sortByKind(): void {
    const start = this.kindStart;
    start.fill(0);
    for (let i = 0; i < this.n; i++) start[Math.min(this.cls[i]!, KIND_COUNT - 1) + 1]!++;
    for (let k = 0; k < KIND_COUNT; k++) start[k + 1]! += start[k]!;
    const next = start.slice(0, KIND_COUNT);
    for (let i = 0; i < this.n; i++) {
      const k = Math.min(this.cls[i]!, KIND_COUNT - 1);
      this.order[next[k]!++] = i;
    }
  }
}
