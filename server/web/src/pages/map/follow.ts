import type { GalaxyPlayerDto } from '../../generated/generated';

export type FollowOutcome =
  | { kind: 'none' }
  /** The followed player is in the viewed sector. */
  | { kind: 'here' }
  /** The followed player is in another sector: switch the view there. */
  | { kind: 'jump'; sectorId: number }
  /** The followed player is not in the galaxy frame (offline, between sectors). The view stays where it is. */
  | { kind: 'lost' };

/** Decides what a sector view following a player does after a galaxy frame (1 Hz): stay, switch sector, or wait. */
export function resolveFollow(followPlayerId: number | null, players: readonly GalaxyPlayerDto[], viewedSector: number): FollowOutcome {
  if (followPlayerId === null) return { kind: 'none' };
  const p = players.find((x) => x.id === followPlayerId);
  if (!p) return { kind: 'lost' };
  return p.sectorId === viewedSector ? { kind: 'here' } : { kind: 'jump', sectorId: p.sectorId };
}

/** The sector view path, with the followed player in the query string. */
export function sectorPath(sectorId: number, followPlayerId: number | null): string {
  return followPlayerId === null ? `/map/${sectorId}` : `/map/${sectorId}?follow=${followPlayerId}`;
}

/** Parses `?follow=` (a positive integer) or null. */
export function parseFollow(value: string | null): number | null {
  if (value === null || !/^\d+$/.test(value)) return null;
  const n = Number(value);
  return Number.isSafeInteger(n) ? n : null;
}
