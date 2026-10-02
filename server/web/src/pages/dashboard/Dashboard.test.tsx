import { act, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DashboardSnapshotDto, PlayerLiveDto } from '../../generated/generated';
import type { GroupSpec } from '../../hub/contract';
import type { GroupHandler } from '../../hub/HubManager';
import { HubProvider } from '../../hub/HubProvider';
import { FakeHubClient, json } from '../../test-utils/fakes';
import { Dashboard } from './Dashboard';

/** Fake hub that also keeps the group handlers so the test can push dashboard events. */
class GroupFakeHub extends FakeHubClient {
  handlers: GroupHandler[] = [];
  override acquire(spec: GroupSpec, handler?: GroupHandler) {
    super.acquire(spec);
    if (handler) this.handlers.push(handler);
    return () => undefined;
  }
  push(event: string, payload: unknown) {
    this.handlers.forEach((h) => h(event, payload));
  }
}

const player = (id: number, over: Partial<PlayerLiveDto> = {}): PlayerLiveDto => ({
  playerId: id,
  connectionId: id,
  name: `Bot${id}`,
  roles: 'Client',
  phase: 'InGame',
  connected: true,
  remoteAddress: '127.0.0.1',
  rttMs: 20,
  fps: 60,
  connectedSeconds: 100,
  muted: false,
  teamId: null,
  teamName: null,
  ...over,
});

const snap = (over: Partial<DashboardSnapshotDto> = {}): DashboardSnapshotDto => ({
  at: '2026-10-02T12:00:00Z',
  session: { id: 1, name: 'Friday Run', state: 'Running', saveName: 'save_017', saveSha256: null, startedAt: null, uptimeSeconds: 3725, players: 2 },
  playersOnline: 2,
  maxPlayers: 8,
  authority: null,
  players: [player(1, { name: 'Alice', roles: 'Authority', fps: 58 }), player(2)],
  entitiesInMirror: 18204,
  traffic: { kBpsIn: 12, kBpsOut: 340, framesInPerSec: 0, framesOutPerSec: 0, droppedRealtimeLast60s: 0, slowConsumerKicksLastHour: 0 },
  sectorsCaptured: 31,
  tickP99Ms: 6.2,
  activeAlerts: [],
  ...over,
});

function mockMetrics(samplesPerSeries: number) {
  const names = ['control', 'realtime', 'bulk'].flatMap((l) => [`net.bytes_in.${l}`, `net.bytes_out.${l}`]);
  const body = names.map((name) => ({
    name,
    unit: 'B/s',
    kind: 'rate',
    intervalSeconds: 1,
    endedAt: null,
    samples: Array.from({ length: samplesPerSeries }, (_, i) => 1024 * (i + 1)),
  }));
  const urls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((...args: unknown[]) => {
      urls.push(String(args[0]));
      return Promise.resolve(json(200, body));
    }),
  );
  return urls;
}

async function setup(samples = 5) {
  const hub = new GroupFakeHub();
  const fetchFn = mockMetrics(samples);
  render(
    <HubProvider client={hub}>
      <Dashboard />
    </HubProvider>,
  );
  await act(async () => {
    await Promise.resolve();
  });
  return { hub, fetchFn };
}

afterEach(() => vi.unstubAllGlobals());

describe('Dashboard', () => {
  it('renders tiles from the snapshot and updates on a Dashboard push', async () => {
    const { hub } = await setup();
    expect(screen.getByText('Waiting for the server…')).toBeInTheDocument();
    act(() => hub.push('$snapshot', snap()));
    expect(within(screen.getByTestId('tile-players')).getByText('2 / 8')).toBeInTheDocument();
    expect(within(screen.getByTestId('tile-session')).getByText('RUNNING')).toBeInTheDocument();
    expect(within(screen.getByTestId('tile-session')).getByText('up 01:02:05')).toBeInTheDocument();
    expect(within(screen.getByTestId('tile-authority')).getByText('58 FPS')).toBeInTheDocument();
    expect(within(screen.getByTestId('tile-authority')).getByText('31 sectors captured')).toBeInTheDocument();

    act(() =>
      hub.push(
        'Dashboard',
        snap({
          sectorsCaptured: 32,
          maxPlayers: 4,
          players: [player(1, { name: 'Alice', roles: 'Authority', fps: 45 })],
          activeAlerts: [{ at: '2026-10-02T12:00:01Z', severity: 'Warning', code: 'AuthorityFps', text: 'Authority FPS low', active: true }],
        }),
      ),
    );
    expect(within(screen.getByTestId('tile-players')).getByText('1 / 4')).toBeInTheDocument();
    expect(within(screen.getByTestId('tile-authority')).getByText('45 FPS')).toBeInTheDocument();
    expect(within(screen.getByTestId('tile-alerts')).getByText('Authority FPS low')).toBeInTheDocument();
  });

  it('updates the player table on PlayerChanged and PlayerRemoved', async () => {
    const { hub } = await setup();
    act(() => hub.push('$snapshot', snap()));
    expect(screen.getByTestId('player-2')).toHaveTextContent('InGame');
    act(() => hub.push('PlayerChanged', player(2, { phase: 'Loading', rttMs: 44 })));
    expect(screen.getByTestId('player-2')).toHaveTextContent('Loading');
    expect(screen.getByTestId('player-2')).toHaveTextContent('44 ms');
    act(() => hub.push('PlayerChanged', player(3, { name: 'Carol' })));
    expect(screen.getByTestId('player-3')).toHaveTextContent('Carol');
    act(() => hub.push('PlayerRemoved', 2));
    expect(screen.queryByTestId('player-2')).not.toBeInTheDocument();
    expect(screen.getByTestId('player-3')).toBeInTheDocument();
  });

  it('shows backfilled sparkline points and appends pushed samples', async () => {
    const { hub, fetchFn } = await setup(5);
    expect(fetchFn[0]).toContain('/api/v1/diagnostics/metrics?series=net.bytes_in.control');
    act(() => hub.push('$snapshot', snap()));
    const chart = () => screen.getByRole('img', { name: /^Traffic, last 10 minutes/ });
    expect(chart()).toHaveAttribute('data-points', '5');
    // 3 lanes x 5 KB/s = 15 KB/s in the newest backfilled sample
    expect(chart().getAttribute('aria-label')).toContain('in 15');
    act(() => hub.push('Dashboard', snap()));
    act(() => hub.push('Dashboard', snap()));
    expect(chart()).toHaveAttribute('data-points', '7');
  });
});
