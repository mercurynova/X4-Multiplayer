import { describe, expect, it } from 'vitest';
import { nearestEntity } from './hitTest';

const x = Float32Array.from([10, 12, 100]);
const y = Float32Array.from([10, 10, 100]);
const order = Int32Array.from([0, 1, 2]);

describe('nearestEntity', () => {
  it('picks the closest within the radius', () => {
    expect(nearestEntity(11.5, 10, x, y, order, 3, 8)).toBe(1);
    expect(nearestEntity(10.2, 10, x, y, order, 3, 8)).toBe(0);
  });
  it('returns -1 when nothing is in range', () => {
    expect(nearestEntity(50, 50, x, y, order, 3, 8)).toBe(-1);
  });
  it('lets priority beat distance and skips filtered entities', () => {
    expect(nearestEntity(10, 10, x, y, order, 3, 8, (i) => (i === 1 ? 3 : 1))).toBe(1);
    expect(nearestEntity(10, 10, x, y, order, 3, 8, (i) => (i === 0 ? -Infinity : 1))).toBe(1);
  });
});
