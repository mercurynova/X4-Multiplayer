import { act, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import { AppRoutes } from '../../AppRoutes';
import type { GalaxyDto, GalaxyFrameDto, GalaxyPlayerDto, SectorFrameDto } from '../../generated/generated';
import { CaptureHub } from '../../test-utils/captureHub';
import { json, mockApi } from '../../test-utils/fakes';
import { niceGridStep } from './SectorCanvas';

const galaxy: GalaxyDto = {
  saveSha256: 'abc',
  clusters: [{ id: 1, macro: 'c1' }],
  sectors: [1, 2, 3].map((id) => ({ id, macro: `s${id}`, name: `Sector ${id}`, clusterId: 1, mapPos: { x: id * 10, y: 0 }, ownerFaction: null })),
  links: [{ fromSector: 1, toSector: 2, kind: 'Gate' }],
};

const player = (id: number, sectorId: number): GalaxyPlayerDto => ({ id, name: `Pilot${id}`, sectorId, pos: { x: 100, y: 0, z: 200 }, headingDeg: 90 });
const gframe = (players: GalaxyPlayerDto[], interest: GalaxyFrameDto['interest'] = []): GalaxyFrameDto => ({
  at: '2026-01-01T00:00:00Z',
  players,
  sectorAgg: [{ sectorId: 1, ships: 10, stations: 2 }],
  interest,
});

function setup(path: string) {
  mockApi(() => json(404, {}));
  const hub = new CaptureHub();
  render(
    <AppProviders initialMe={{ username: 'admin', role: 'Admin', mustChangePassword: false }} hub={hub}>
      <MemoryRouter initialEntries={[path]}>
        <AppRoutes />
      </MemoryRouter>
    </AppProviders>,
  );
  return hub;
}

beforeEach(() => {
  // jsdom has no canvas; the draw loop treats a missing context as "nothing to draw".
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('galaxy page', () => {
  it('waits for galaxy metadata, then shows the map and the live players', async () => {
    const hub = setup('/map');
    expect(await screen.findByTestId('galaxy-waiting')).toBeInTheDocument();
    expect(hub.keys).toContain('galaxy');
    act(() => hub.push('$snapshot', galaxy, 'galaxy'));
    expect(await screen.findByTestId('galaxy-canvas')).toBeInTheDocument();
    act(() => hub.push('GalaxyFrame', gframe([player(1, 1), player(2, 3)])));
    expect(screen.getByRole('button', { name: 'Follow Pilot1' })).toBeInTheDocument();
    expect(screen.getByText(/Sector 3 \(0\.1, 0\.2 km\)/)).toBeInTheDocument();
  });
});

describe('sector page', () => {
  it('subscribes to the sector topic, counts entities from frames and ignores frames of other sectors', async () => {
    const hub = setup('/map/2');
    expect(hub.keys).toContain('sector:2');
    const f: SectorFrameDto = { sectorId: 2, tick: 7, ids: [1, 2], x: [0, 10], z: [0, 10], yaw: [0, 0], cls: [3, 6], flags: [0, 0], playerIds: [null, null] };
    act(() => hub.push('SectorFrame', f));
    expect(await screen.findByText('Entities: 2')).toBeInTheDocument();
    act(() => hub.push('SectorFrame', { ...f, sectorId: 9, ids: [1, 2, 3], x: [0, 1, 2], z: [0, 1, 2], yaw: [0, 0, 0], cls: [3, 3, 3], flags: [0, 0, 0], playerIds: [null, null, null] }));
    expect(screen.getByText('Entities: 2')).toBeInTheDocument();
  });

  it('shows which clients have the sector in which interest tier', () => {
    const hub = setup('/map/2');
    act(() => hub.push('GalaxyFrame', gframe([player(1, 2), player(2, 5)], [
      { playerId: 1, sectorIds: [2], tier: 'Near' },
      { playerId: 2, sectorIds: [2, 3], tier: 'Adjacent' },
      { playerId: 2, sectorIds: [9], tier: 'Linger' },
    ])));
    const list = screen.getByTestId('interest-list');
    expect(list).toHaveTextContent('Pilot1');
    expect(list).toHaveTextContent('Near');
    expect(list).toHaveTextContent('Pilot2');
    expect(list).toHaveTextContent('Adjacent');
    expect(list).not.toHaveTextContent('Linger');
  });

  it('follows a player across a gate jump: the view switches to the new sector and its topic', async () => {
    const hub = setup('/map/2?follow=1');
    act(() => hub.push('GalaxyFrame', gframe([player(1, 2)])));
    expect(screen.getByTestId('following')).toHaveTextContent('Following Pilot1');
    expect(hub.keys).toContain('sector:2');
    act(() => hub.push('GalaxyFrame', gframe([player(1, 3)])));
    expect(await screen.findByTestId('sector-name')).toHaveTextContent('Sector 3');
    expect(hub.keys).toContain('sector:3');
    expect(hub.keys).not.toContain('sector:2');
    expect(screen.getByTestId('following')).toBeInTheDocument(); // still following after the jump
  });

  it('shows a hub error such as the two-view cap', async () => {
    const hub = setup('/map/2');
    act(() => hub.push('$error', new Error('TooManySectors: at most 2 sector views per admin'), 'sector:2'));
    expect(await screen.findByTestId('sector-error')).toHaveTextContent('TooManySectors');
  });

  it('rejects a non-numeric sector id', () => {
    setup('/map/abc');
    expect(screen.getByRole('alert')).toHaveTextContent('Not a sector id');
  });
});

describe('grid step', () => {
  it('picks a 1-2-5 step giving 80-160 px between lines', () => {
    for (const zoom of [0.0005, 0.003, 0.02, 0.4, 3]) {
      const px = niceGridStep(zoom) * zoom;
      expect(px).toBeGreaterThanOrEqual(50);
      expect(px).toBeLessThanOrEqual(260);
    }
    expect(niceGridStep(0.1)).toBe(1000);
  });
});
