import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import type { ConnectionStatsDto, LaneStatsDto, MetricSeriesDto } from '../../generated/generated';
import { CaptureHub } from '../../test-utils/captureHub';
import { json, mockApi } from '../../test-utils/fakes';
import { DiagnosticsPage, slowReason } from './DiagnosticsPage';

afterEach(() => vi.unstubAllGlobals());

const lane = (o: Partial<LaneStatsDto> = {}): LaneStatsDto => ({ bytesIn: 0, bytesOut: 0, dropped: 0, coalesced: 0, maxQueuedBytes: 0, ...o });
const conn = (id: number, player: string, o: Partial<ConnectionStatsDto> = {}): ConnectionStatsDto => ({
  connectionId: id,
  player,
  roles: 'Client',
  transport: 'TCP',
  remote: '127.0.0.1:5000',
  ageSeconds: 30,
  rttMs: 12,
  bytesIn: 100,
  bytesOut: 1000,
  framesIn: 5,
  framesOut: 50,
  coalesced: 0,
  dropped: 0,
  violations: 0,
  inboundDropped: 0,
  flushAvgMs: 0.2,
  flushMaxMs: 1.5,
  control: lane(),
  realtime: lane(),
  bulk: lane(),
  ...o,
});

const series: MetricSeriesDto[] = [
  { name: 'net.bytes_out.realtime', unit: 'B/s', kind: 'rate', intervalSeconds: 1, endedAt: null, samples: [1, 5, 2, 9] },
  { name: 'net.connections', unit: 'connections', kind: 'gauge', intervalSeconds: 1, endedAt: null, samples: [1, 2, 3] },
];

function setup(initial: ConnectionStatsDto[] = []) {
  const calls = mockApi((c) => {
    if (c.url === '/api/v1/diagnostics/connections' && c.method === 'GET') return json(200, initial);
    if (c.url.startsWith('/api/v1/diagnostics/metrics')) return json(200, series);
    return json(204, null);
  });
  const hub = new CaptureHub();
  render(
    <AppProviders initialMe={{ username: 'admin', role: 'Admin', mustChangePassword: false }} hub={hub}>
      <DiagnosticsPage />
    </AppProviders>,
  );
  return { calls, hub };
}

describe('slowReason', () => {
  it('is null for a healthy connection and names the lane for a backed-up one', () => {
    expect(slowReason(conn(1, 'A'))).toBeNull();
    expect(slowReason(conn(2, 'B', { realtime: lane({ maxQueuedBytes: 40 * 1024 }) }))).toMatch(/realtime queue peaked at 40 KiB/);
    expect(slowReason(conn(3, 'C'), 4)).toMatch(/4 frame/);
  });
});

describe('DiagnosticsPage', () => {
  it('subscribes to diagnostics and renders a row per pushed connection', async () => {
    const { hub } = setup();
    expect(hub.keys).toContain('diag');
    act(() => hub.push('Diagnostics', [conn(12, 'Alice', { transport: 'TCP+UDP', rttMs: 31 }), conn(17, 'Bob')]));
    const rows = screen.getAllByRole('row');
    expect(rows).toHaveLength(3); // header + 2
    expect(within(rows[1]!).getByText('Alice')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('active')).toBeInTheDocument();
    expect(within(rows[2]!).getByText('TCP only')).toBeInTheDocument();
    expect(screen.getByText(/2 connection\(s\)/)).toBeInTheDocument();
  });

  it('highlights a slow reader and counts it', () => {
    const { hub } = setup();
    act(() => hub.push('Diagnostics', [conn(1, 'Fast'), conn(2, 'Slowpoke', { realtime: lane({ maxQueuedBytes: 900 * 1024, dropped: 7 }), dropped: 7 })]));
    const slowRow = screen.getByText('Slowpoke').closest('tr')!;
    expect(slowRow).toHaveClass('row-slow');
    expect(within(slowRow).getByText(/Slow reader/)).toBeInTheDocument();
    expect(screen.getByText('Fast').closest('tr')).not.toHaveClass('row-slow');
    expect(screen.getByText(/1 slow reader/)).toBeInTheDocument();
  });

  it('draws the metric charts from the metrics endpoint', async () => {
    const { calls } = setup();
    expect(await screen.findByRole('img', { name: /Bytes out per lane, peak 9/ })).toBeInTheDocument();
    expect(calls.some((c) => c.url.startsWith('/api/v1/diagnostics/metrics?window='))).toBe(true);
    expect(screen.getByRole('img', { name: /Connections, peak 3/ })).toBeInTheDocument();
  });

  it('toggles tracing for the selected connection', async () => {
    const user = userEvent.setup();
    const { hub, calls } = setup();
    act(() => hub.push('Diagnostics', [conn(17, 'Bob')]));
    await user.click(screen.getByRole('button', { name: '17' }));
    await user.click(screen.getByLabelText(/Trace 1 in 100 frames/));
    const post = calls.find((c) => c.method === 'POST');
    expect(post?.url).toBe('/api/v1/diagnostics/connections/17/trace');
    expect(JSON.parse(post?.body ?? '{}')).toEqual({ enabled: true, sampleEvery: 100 });
  });
});
