import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import type { LogEntryDto } from '../../generated/generated';
import { CaptureHub } from '../../test-utils/captureHub';
import { json, mockApi, type Call } from '../../test-utils/fakes';
import { LogsPage } from './LogsPage';

afterEach(() => vi.unstubAllGlobals());

const line = (seq: number, source: string, message: string, level = 'Information'): LogEntryDto => ({
  seq,
  at: '2026-01-01T10:00:00.123Z',
  level,
  source,
  message,
  exception: null,
  props: { ConnectionId: '17' },
});

function setup(role = 'Admin', handler: (c: Call) => Response = () => json(200, [])) {
  const calls = mockApi(handler);
  const hub = new CaptureHub();
  render(
    <AppProviders initialMe={{ username: 'admin', role, mustChangePassword: false }} hub={hub}>
      <LogsPage />
    </AppProviders>,
  );
  return { calls, hub };
}

const logKeys = (hub: CaptureHub) => hub.keys.filter((k) => k.startsWith('logs:'));

describe('LogsPage', () => {
  it('shows the backfill and live batches from the logs group', async () => {
    const { hub } = setup();
    expect(logKeys(hub)).toHaveLength(1);
    act(() => hub.push('$snapshot', [line(1, 'Session', 'Player Bob joined')]));
    expect(screen.getByText(/Player Bob joined/)).toBeInTheDocument();
    act(() => hub.push('LogBatch', [line(2, 'Net', 'lane dropped frames', 'Warning')]));
    expect(screen.getByText(/lane dropped frames/)).toBeInTheDocument();
    expect(screen.getByText(/2 lines/)).toBeInTheDocument();
  });

  it('resubscribes with the level, source and text filters (server-side)', async () => {
    const user = userEvent.setup();
    const { hub } = setup();
    await user.selectOptions(screen.getByLabelText('Level'), 'Warning');
    await user.type(screen.getByLabelText('Source'), 'node:Alice');
    await user.type(screen.getByLabelText('Search'), 'saved');
    await waitFor(() => {
      const keys = logKeys(hub);
      expect(keys).toHaveLength(1);
      expect(JSON.parse(keys[0]!.slice('logs:'.length))).toEqual({ level: 'Warning', source: 'node:Alice', q: 'saved' });
    });
  });

  it('buffers lines while paused and flushes them on resume', async () => {
    const user = userEvent.setup();
    const { hub } = setup();
    act(() => hub.push('$snapshot', [line(1, 'Session', 'first')]));
    await user.click(screen.getByRole('button', { name: 'Pause' }));
    act(() => hub.push('LogBatch', [line(2, 'Session', 'while paused')]));
    expect(screen.queryByText(/while paused/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Resume \(1 new\)/ }));
    expect(screen.getByText(/while paused/)).toBeInTheDocument();
  });

  it('expands a row to show its properties and exception', async () => {
    const user = userEvent.setup();
    const { hub } = setup();
    act(() => hub.push('$snapshot', [{ ...line(5, 'Protocol', 'frame too large', 'Error'), exception: 'System.InvalidOperationException: boom' }]));
    await user.click(screen.getByRole('button', { name: /frame too large/ }));
    expect(screen.getByText('ConnectionId')).toBeInTheDocument();
    expect(screen.getByText(/InvalidOperationException/)).toBeInTheDocument();
  });

  it('queries older lines before the oldest shown sequence', async () => {
    const user = userEvent.setup();
    const { hub, calls } = setup('Admin', (c) => (c.url.startsWith('/api/v1/logs?') ? json(200, [line(3, 'Old', 'older line')]) : json(200, [])));
    act(() => hub.push('$snapshot', [line(10, 'Session', 'newest')]));
    await user.click(screen.getByRole('button', { name: 'Load older' }));
    expect(await screen.findByText(/older line/)).toBeInTheDocument();
    expect(calls.find((c) => c.url.startsWith('/api/v1/logs?'))?.url).toContain('before=10');
  });

  it('offers the file download to admins only', () => {
    setup('Admin');
    expect(screen.getByRole('link', { name: 'Download log file' }).getAttribute('href')).toMatch(/^\/api\/v1\/logs\/download\?date=\d{4}-\d{2}-\d{2}$/);
  });

  it('hides the download from viewers', () => {
    setup('Viewer');
    expect(screen.queryByRole('link', { name: 'Download log file' })).not.toBeInTheDocument();
  });
});
