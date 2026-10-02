import { describe, expect, it } from 'vitest';
import type { GalaxyPlayerDto } from '../../generated/generated';
import { parseFollow, resolveFollow, sectorPath } from './follow';

const p = (id: number, sectorId: number): GalaxyPlayerDto => ({ id, name: `P${id}`, sectorId, pos: { x: 0, y: 0, z: 0 }, headingDeg: 0 });

describe('follow on jump', () => {
  it('does nothing when nobody is followed', () => {
    expect(resolveFollow(null, [p(1, 5)], 5)).toEqual({ kind: 'none' });
  });
  it('stays while the player is in the viewed sector', () => {
    expect(resolveFollow(1, [p(1, 5), p(2, 9)], 5)).toEqual({ kind: 'here' });
  });
  it('switches to the new sector after the player jumps', () => {
    expect(resolveFollow(1, [p(1, 7)], 5)).toEqual({ kind: 'jump', sectorId: 7 });
  });
  it('keeps the view when the player is missing from the frame', () => {
    expect(resolveFollow(3, [p(1, 7)], 5)).toEqual({ kind: 'lost' });
  });
  it('builds and parses the follow path', () => {
    expect(sectorPath(7, null)).toBe('/map/7');
    expect(sectorPath(7, 3)).toBe('/map/7?follow=3');
    expect(parseFollow('3')).toBe(3);
    expect(parseFollow('x')).toBeNull();
    expect(parseFollow(null)).toBeNull();
    expect(parseFollow('-1')).toBeNull();
  });
});
