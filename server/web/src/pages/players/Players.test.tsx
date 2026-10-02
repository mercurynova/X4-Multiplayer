import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import { AppRoutes } from '../../AppRoutes';
import type { PlayerDto, PlayerLiveDto } from '../../generated/generated';
import type { GroupHandler } from '../../hub/HubManager';
import type { GroupSpec } from '../../hub/contract';
import { FakeHubClient, json, mockApi, type Call } from '../../test-utils/fakes';

/** FakeHubClient that also lets a test push group events (the base fake only records the acquire). */
class GroupHub extends FakeHubClient {
  handlers: GroupHandler[] = [];
  override acquire(spec: GroupSpec, handler?: GroupHandler) {
    if (handler) this.handlers.push(handler);
    return super.acquire(spec);
  }
  push(event: string, payload: unknown) {
    this.handlers.forEach((h) => h(event, payload));
  }
}

const player = (id: number, name: string, extra: Partial<PlayerDto> = {}): PlayerDto => ({
  id,
  name,
  firstSeen: '2026-01-01T00:00:00Z',
  lastSeen: '2026-01-02T00:00:00Z',
  totalPlaytimeSeconds: 3700,
  online: false,
  muted: false,
  mutedUntil: null,
  activeBan: null,
  lastIp: '10.0.0.' + id,
  notes: null,
  teamId: null,
  teamName: null,
  ...extra,
});

const live = (playerId: number, name: string): PlayerLiveDto => ({
  playerId,
  connectionId: playerId,
  name,
  roles: 'client',
  phase: 'InGame',
  connected: true,
  remoteAddress: '10.0.0.' + playerId,
  rttMs: 31,
  fps: 60,
  connectedSeconds: 10,
  muted: false,
  teamId: null,
  teamName: null,
});

let calls: Call[];

function setup(path: string, handler: (c: Call) => Response, role = 'Admin') {
  calls = mockApi(handler);
  const hub = new GroupHub();
  render(
    <AppProviders initialMe={{ username: 'admin', role, mustChangePassword: false }} hub={hub}>
      <MemoryRouter initialEntries={[path]}>
        <AppRoutes />
      </MemoryRouter>
    </AppProviders>,
  );
  return hub;
}

afterEach(() => vi.unstubAllGlobals());

describe('players list', () => {
  const bob = player(1, 'Bob', { online: true });
  const eve = player(2, 'Eve', { activeBan: { id: 9, playerId: 2, playerName: 'Eve', ipCidr: null, reason: 'spam', createdBy: 'admin', createdAt: '', expiresAt: null, active: true } });

  it('renders online and offline players and follows PlayerChanged / PlayerRemoved', async () => {
    const hub = setup('/players', () => json(200, [bob, eve]));
    const bobRow = await screen.findByRole('row', { name: /Bob/ });
    expect(within(bobRow).getByText(/Online/)).toBeInTheDocument();
    const eveRow = screen.getByRole('row', { name: /Eve/ });
    expect(within(eveRow).getByText('Offline')).toBeInTheDocument();
    expect(within(eveRow).getByText('BAN')).toBeInTheDocument();
    expect(within(eveRow).getByRole('button', { name: 'Unban Eve' })).toBeInTheDocument();

    // Eve comes online: the push flips her row without a reload.
    act(() => hub.push('PlayerChanged', live(2, 'Eve')));
    expect(within(screen.getByRole('row', { name: /Eve/ })).getByText(/Online 31 ms/)).toBeInTheDocument();

    // Bob leaves (the dashboard snapshot told us who is connected, so the roster is live-driven from here).
    act(() => hub.push('Dashboard', { players: [live(1, 'Bob'), live(2, 'Eve')] }));
    act(() => hub.push('PlayerRemoved', 1));
    expect(within(screen.getByRole('row', { name: /Bob/ })).getByText('Offline')).toBeInTheDocument();
    expect(within(screen.getByRole('row', { name: /Bob/ })).queryByRole('button', { name: 'Kick Bob' })).not.toBeInTheDocument();
  });

  it('adds a player nobody has seen yet by reloading the list', async () => {
    let list = [bob];
    const hub = setup('/players', () => json(200, list));
    await screen.findByRole('row', { name: /Bob/ });
    list = [bob, player(3, 'Cy', { online: true })];
    act(() => hub.push('PlayerChanged', live(3, 'Cy')));
    expect(await screen.findByRole('row', { name: /Cy/ })).toBeInTheDocument();
  });

  it('filters by name and by online', async () => {
    const hub = setup('/players', () => json(200, [bob, eve]));
    await screen.findByRole('row', { name: /Bob/ });
    act(() => hub.push('Dashboard', { players: [live(1, 'Bob')] }));
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Online' }));
    expect(screen.queryByRole('row', { name: /Eve/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'All' }));
    await user.type(screen.getByRole('searchbox', { name: 'Search players' }), 'ev');
    expect(screen.queryByRole('row', { name: /Bob/ })).not.toBeInTheDocument();
    expect(screen.getByRole('row', { name: /Eve/ })).toBeInTheDocument();
  });

  it('hides the actions from a Viewer', async () => {
    setup('/players', () => json(200, [bob]), 'Viewer');
    await screen.findByRole('row', { name: /Bob/ });
    expect(screen.queryByRole('button', { name: /Kick/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Ban IP/ })).not.toBeInTheDocument();
  });

  it('requires a reason to kick, then sends it', async () => {
    const hub = setup('/players', (c) => (c.method === 'POST' ? new Response(null, { status: 202 }) : json(200, [bob])));
    await screen.findByRole('row', { name: /Bob/ });
    act(() => hub.push('PlayerChanged', live(1, 'Bob')));
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Kick Bob' }));
    const dialog = screen.getByRole('dialog', { name: 'Kick Bob' });
    await user.click(within(dialog).getByRole('button', { name: 'Kick' }));
    expect(within(dialog).getByText('A reason is required.')).toBeInTheDocument();
    expect(calls.some((c) => c.method === 'POST')).toBe(false);

    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'afk');
    await user.click(within(dialog).getByRole('button', { name: 'Kick' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const post = calls.find((c) => c.method === 'POST');
    expect(post?.url).toBe('/api/v1/players/1/kick');
    expect(JSON.parse(post?.body ?? '{}')).toEqual({ reason: 'afk' });
  });

  it('requires a reason to ban and maps an ApiProblem 400 to the fields', async () => {
    setup('/players', (c) =>
      c.method === 'POST'
        ? json(400, {
            title: 'One or more validation errors occurred.',
            status: 400,
            code: 'ValidationFailed',
            errors: { IpCidr: ['Use an address or a CIDR block such as 10.1.0.0/16.'], Reason: ['Give a reason.'] },
          })
        : json(200, [bob]),
    );
    await screen.findByRole('row', { name: /Bob/ });
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Ban IP or key…' }));
    const dialog = screen.getByRole('dialog');

    // Reason is checked on the client first.
    await user.type(within(dialog).getByLabelText(/Address or CIDR/), 'nonsense');
    await user.click(within(dialog).getByRole('button', { name: 'Ban' }));
    expect(within(dialog).getByText('A reason is required.')).toBeInTheDocument();
    expect(calls.some((c) => c.method === 'POST')).toBe(false);

    // The server's 400 lands on the matching fields.
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'griefing');
    await user.click(within(dialog).getByRole('button', { name: 'Ban' }));
    expect(await within(dialog).findByText('Use an address or a CIDR block such as 10.1.0.0/16.')).toBeInTheDocument();
    expect(within(dialog).getByText('Give a reason.')).toBeInTheDocument();
    expect(within(dialog).getByLabelText(/Address or CIDR/)).toHaveAttribute('aria-invalid', 'true');
    const post = calls.find((c) => c.method === 'POST');
    expect(post?.url).toBe('/api/v1/bans');
    expect(JSON.parse(post?.body ?? '{}')).toMatchObject({ ipCidr: 'nonsense', reason: 'griefing', durationMinutes: null });
  });

  it('bans a player for a duration, with the address too', async () => {
    setup('/players', (c) => (c.method === 'POST' ? json(201, {}) : json(200, [bob])));
    await screen.findByRole('row', { name: /Bob/ });
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Ban Bob' }));
    const dialog = screen.getByRole('dialog', { name: 'Ban Bob' });
    await user.selectOptions(within(dialog).getByLabelText('Duration'), '1 day');
    await user.click(within(dialog).getByLabelText(/Also ban the address/));
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'cheating');
    await user.click(within(dialog).getByRole('button', { name: 'Ban' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(JSON.parse(calls.find((c) => c.method === 'POST')?.body ?? '{}')).toEqual({
      playerId: 1,
      keyHash: null,
      ipCidr: '10.0.0.1/32',
      reason: 'cheating',
      durationMinutes: 1440,
    });
  });

  it('unbans from the list', async () => {
    setup('/players', (c) => (c.method === 'DELETE' ? new Response(null, { status: 204 }) : json(200, [bob, eve])));
    await screen.findByRole('row', { name: /Eve/ });
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Unban Eve' }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Unban' }));
    await waitFor(() => expect(calls.find((c) => c.method === 'DELETE')?.url).toBe('/api/v1/bans/9'));
  });
});

describe('player detail', () => {
  const detail = (extra: object = {}) => ({
    player: player(1, 'Bob', { notes: 'hello' }),
    keyHash: 'ab'.repeat(32),
    live: live(1, 'Bob'),
    history: [{ sessionId: 1, sessionName: 'Friday Run', joinedAt: '2026-01-01T00:00:00Z', leftAt: null, leaveReason: null, role: 'client' }],
    bans: [],
    ...extra,
  });

  beforeEach(() => undefined);

  it('shows identity, connection, notes and history, and saves notes', async () => {
    setup('/players/1', (c) => (c.method === 'PATCH' ? json(200, player(1, 'Bob')) : json(200, detail())));
    expect(await screen.findByRole('heading', { name: 'Bob' })).toBeInTheDocument();
    expect(screen.getByText('ab'.repeat(32))).toBeInTheDocument();
    expect(screen.getByText('31 ms')).toBeInTheDocument();
    expect(screen.getByText('Friday Run')).toBeInTheDocument();
    const notes = screen.getByRole('textbox', { name: 'Admin notes' });
    expect(notes).toHaveValue('hello');
    const user = userEvent.setup();
    await user.type(notes, '!');
    await user.click(screen.getByRole('button', { name: 'Save notes' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true));
    expect(JSON.parse(calls.find((c) => c.method === 'PATCH')?.body ?? '{}')).toEqual({ notes: 'hello!', releaseName: null });
  });

  it('shows a notes ApiProblem as a field error', async () => {
    setup('/players/1', (c) =>
      c.method === 'PATCH'
        ? json(400, { title: 'bad', status: 400, code: 'ValidationFailed', errors: { notes: ['The notes can be at most 1000 characters.'] } })
        : json(200, detail()),
    );
    await screen.findByRole('heading', { name: 'Bob' });
    const user = userEvent.setup();
    await user.type(screen.getByRole('textbox', { name: 'Admin notes' }), '!');
    await user.click(screen.getByRole('button', { name: 'Save notes' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('at most 1000 characters');
  });

  it('release name needs the player offline', async () => {
    setup('/players/1', () => json(200, detail()));
    await screen.findByRole('heading', { name: 'Bob' });
    expect(screen.getByRole('button', { name: 'Release name' })).toBeDisabled();
  });

  it('follows PlayerRemoved to offline and revokes a ban', async () => {
    const ban = { id: 4, playerId: 1, playerName: 'Bob', ipCidr: null, reason: 'x', createdBy: 'admin', createdAt: '2026-01-01T00:00:00Z', expiresAt: null, active: true };
    let removed = false;
    const hub = setup('/players/1', (c) =>
      c.method === 'DELETE'
        ? new Response(null, { status: 204 })
        : json(200, detail({ bans: [ban], live: removed ? null : live(1, 'Bob'), player: player(1, 'Bob', { activeBan: ban }) })),
    );
    await screen.findByRole('heading', { name: 'Bob' });
    expect(screen.getByText('Online')).toBeInTheDocument();
    removed = true;
    act(() => hub.push('PlayerRemoved', 1));
    await waitFor(() => expect(screen.getByText('Offline')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Release name' })).toBeEnabled();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Revoke ban 4' }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Revoke ban' }));
    await waitFor(() => expect(calls.find((c) => c.method === 'DELETE')?.url).toBe('/api/v1/bans/4'));
  });

  it('shows a 404 as a message', async () => {
    setup('/players/77', () => json(404, { title: 'Not found', status: 404, code: 'NotFound' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/Not found|No such player/);
  });
});
