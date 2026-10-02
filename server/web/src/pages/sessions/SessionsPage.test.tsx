import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import type { SaveDto, SessionDetailDto, SessionSummaryDto, TransferProgressDto } from '../../generated/generated';
import type { GroupSpec } from '../../hub/contract';
import { FakeHubClient, json, mockApi, type Call } from '../../test-utils/fakes';
import { SessionsPage } from './SessionsPage';

const SHA_A = 'a'.repeat(64);
const SHA_B = 'b'.repeat(64);

const save = (over: Partial<SaveDto> = {}): SaveDto => ({
  sha256: SHA_A,
  sizeBytes: 88 * 1024 * 1024,
  displayName: 'save_017',
  source: 'authority',
  uploadedAt: '2026-09-29T20:00:00Z',
  gameVersion: '9.00',
  saveTime: null,
  playerName: null,
  pinned: false,
  ghostsCleaned: true,
  current: false,
  ...over,
});

const session = (state: string, over: Partial<SessionDetailDto> = {}): SessionDetailDto => ({
  id: 7,
  name: 'Friday Run',
  state,
  saveName: 'save_017',
  saveSha256: SHA_A,
  createdAt: '2026-09-29T19:00:00Z',
  startedAt: '2026-09-29T19:21:00Z',
  endedAt: null,
  endReason: null,
  uptimeSeconds: 3725,
  players: 2,
  live: true,
  phaseSince: null,
  authority: { playerId: 1, name: 'Alice', status: 'Connected', graceRemainingSeconds: null, gameBuild: null, modVersion: null },
  nodes: [],
  ...over,
});

/** FakeHubClient that also delivers pushes to group handlers (what the real HubManager does for acquired groups). */
class GroupHub extends FakeHubClient {
  private handlers = new Set<(event: string, payload: unknown) => void>();
  override acquire(spec: GroupSpec, handler?: (event: string, payload: unknown) => void): () => undefined {
    super.acquire(spec);
    if (handler) this.handlers.add(handler);
    return () => {
      if (handler) this.handlers.delete(handler);
      return undefined;
    };
  }
  push(event: string, payload: unknown) {
    this.handlers.forEach((h) => h(event, payload));
  }
}

interface World {
  current: SessionDetailDto | null;
  saves: SaveDto[];
  history: SessionSummaryDto[];
  onOther?: (c: Call) => Response | undefined;
}

function setup(world: World, role = 'Admin') {
  const hub = new GroupHub();
  const calls = mockApi((c) => {
    const path = c.url.split('?')[0]!;
    if (c.method === 'GET' && path === '/api/v1/sessions/current') return world.current ? json(200, world.current) : new Response(null, { status: 204 });
    if (c.method === 'GET' && path === '/api/v1/sessions') return json(200, world.history);
    if (c.method === 'GET' && path === '/api/v1/saves') return json(200, world.saves);
    return world.onOther?.(c) ?? json(202, {});
  });
  // mockApi answers /sessions/current itself with 204; route it to the world instead.
  const inner = globalThis.fetch as unknown as (u: string, i?: RequestInit) => Promise<Response>;
  vi.stubGlobal('fetch', (u: string, i?: RequestInit) =>
    u === '/api/v1/sessions/current'
      ? Promise.resolve(world.current ? json(200, world.current) : new Response(null, { status: 204 }))
      : inner(u, i),
  );
  render(
    <AppProviders initialMe={{ username: 'admin', role, mustChangePassword: false }} hub={hub}>
      <SessionsPage />
    </AppProviders>,
  );
  return { hub, calls };
}

afterEach(() => vi.unstubAllGlobals());

describe('SessionsPage', () => {
  it('shows the current session, saves and history', async () => {
    setup({
      current: session('Running'),
      saves: [save({ current: true }), save({ sha256: SHA_B, displayName: 'mystart', source: 'admin-upload', ghostsCleaned: false })],
      history: [{ id: 6, name: 'Old', state: 'Ended', saveName: null, saveSha256: null, startedAt: null, uptimeSeconds: 0, players: 0 }],
    });
    expect(await screen.findByText('Friday Run')).toBeInTheDocument();
    expect(screen.getByText('RUNNING')).toBeInTheDocument();
    expect(screen.getByText('01:02:05')).toBeInTheDocument();
    expect(screen.getByText('Alice (Connected)')).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'Download mystart' })).toBeInTheDocument();
    expect(screen.getByText('in use')).toBeInTheDocument();
    expect(screen.getByText('ghosts not cleaned')).toBeInTheDocument();
    expect(screen.getByText('Old')).toBeInTheDocument();
  });

  it('asks for confirmation, then requests a save', async () => {
    const { calls } = setup({ current: session('Running'), saves: [], history: [] });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Request save now' }));
    expect(calls.some((c) => c.url.endsWith('/request-save'))).toBe(false); // nothing before the confirm
    const dialog = screen.getByRole('alertdialog', { name: /save now/i });
    await user.click(within(dialog).getByRole('button', { name: 'Request save' }));
    await waitFor(() => expect(calls.find((c) => c.url === '/api/v1/sessions/7/request-save')?.method).toBe('POST'));
  });

  it('stops with the final-save choice after a confirm, and cancel does nothing', async () => {
    const { calls } = setup({ current: session('Running'), saves: [], history: [] });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Stop session' }));
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Cancel' }));
    expect(calls.some((c) => c.url.endsWith('/stop'))).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Stop session' }));
    const dialog = screen.getByRole('alertdialog');
    await user.click(within(dialog).getByRole('checkbox')); // untick the final save
    await user.click(within(dialog).getByRole('button', { name: 'Stop session' }));
    await waitFor(() => {
      const stop = calls.find((c) => c.url === '/api/v1/sessions/7/stop');
      expect(stop?.method).toBe('POST');
      expect(JSON.parse(stop!.body!)).toEqual({ requestFinalSave: false });
    });
  });

  it('starts an idle session after a confirm and disables request-save/stop', async () => {
    const { calls } = setup({ current: session('Idle', { startedAt: null }), saves: [], history: [] });
    const user = userEvent.setup();
    expect(await screen.findByRole('button', { name: 'Request save now' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Stop session' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Start session' }));
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Start' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/sessions/7/start' && c.method === 'POST')).toBe(true));
  });

  it('creates a session with the selected save and starts it', async () => {
    const created = session('Idle', { id: 9, name: 'Weekend', startedAt: null });
    const { calls } = setup({
      current: null,
      saves: [save()],
      history: [],
      onOther: (c) => (c.url === '/api/v1/sessions' ? json(201, created) : undefined),
    });
    const user = userEvent.setup();
    await screen.findByRole('link', { name: 'Download save_017' });
    await user.click(screen.getByRole('button', { name: 'Use save_017 for the next session' }));
    await user.type(screen.getByLabelText('Name'), 'Weekend');
    await user.click(screen.getByRole('button', { name: 'Create and start' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/sessions/9/start')).toBe(true));
    const create = calls.find((c) => c.url === '/api/v1/sessions' && c.method === 'POST')!;
    expect(JSON.parse(create.body!)).toMatchObject({ name: 'Weekend', saveId: SHA_A });
  });

  it('deletes a save after a confirm and shows the protection error when the server refuses', async () => {
    const { calls } = setup({
      current: null,
      saves: [save()],
      history: [],
      onOther: (c) =>
        c.method === 'DELETE'
          ? json(409, { code: 'InUse', title: 'Conflict.', detail: 'A session uses this save.', status: 409 })
          : undefined,
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Delete save_017' }));
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Delete save' }));
    expect(await screen.findByText('A session uses this save.')).toBeInTheDocument();
    expect(calls.find((c) => c.method === 'DELETE')?.url).toBe(`/api/v1/saves/${SHA_A}`);
  });

  it('hides a deleted save at once even if the (write-behind) list still returns it', async () => {
    const { calls } = setup({ current: null, saves: [save()], history: [], onOther: (c) => (c.method === 'DELETE' ? new Response(null, { status: 204 }) : undefined) });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Delete save_017' }));
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Delete save' }));
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Download save_017' })).toBeNull());
    expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(1);
  });

  it('renders SaveTransfer pushes as per-player progress and updates them', async () => {
    const { hub } = setup({ current: session('Running'), saves: [], history: [] });
    await screen.findByText('Friday Run');
    const t: TransferProgressDto = {
      id: 3, isUpload: false, playerId: 2, playerName: 'Carol', sha256: SHA_A, kind: 'Save', size: 1000, done: 640,
      startOffset: 0, startedAt: '2026-09-29T20:00:00Z', finished: false,
    };
    act(() => hub.push('SaveTransfer', t));
    expect(screen.getByText(/Download to Carol/)).toBeInTheDocument();
    expect(screen.getByText(/64%/)).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: /Carol/ })).toHaveAttribute('value', '640');
    act(() => hub.push('SaveTransfer', { ...t, done: 1000, finished: true }));
    expect(screen.getByText(/100%/)).toBeInTheDocument();
    expect(screen.getAllByText(/Download to Carol/)).toHaveLength(1); // updated in place, not duplicated
  });

  it('hides mutating controls for viewers', async () => {
    setup({ current: session('Running'), saves: [save()], history: [] }, 'Viewer');
    await screen.findByRole('link', { name: 'Download save_017' });
    expect(screen.queryByRole('button', { name: 'Stop session' })).toBeNull();
    expect(screen.queryByText('Upload a save (.xml.gz) or drop it here')).toBeNull();
    expect(screen.getByRole('link', { name: 'Download save_017' })).toHaveAttribute('href', `/api/v1/saves/${SHA_A}/download`);
  });
});
