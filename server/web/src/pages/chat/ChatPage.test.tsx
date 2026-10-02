import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import type { ChatMessageDto } from '../../generated/generated';
import { CaptureHub } from '../../test-utils/captureHub';
import { json, mockApi, type Call } from '../../test-utils/fakes';
import { ChatPage } from './ChatPage';

afterEach(() => vi.unstubAllGlobals());

const msg = (id: number, from: string, text: string, extra: Partial<ChatMessageDto> = {}): ChatMessageDto => ({
  id,
  at: `2026-01-01T10:00:0${id > 0 ? id : -id}Z`,
  from,
  fromAdmin: false,
  channel: 'all',
  text,
  ...extra,
});

function setup(role = 'Admin', handler?: (c: Call) => Response) {
  const calls = mockApi((c) => {
    if (handler) {
      const r = handler(c);
      if (r) return r;
    }
    if (c.url.startsWith('/api/v1/chat') && c.method === 'GET') return json(200, [msg(1, 'Alice', 'hello there'), msg(2, 'Bob', 'anyone at the shipyard?')]);
    if (c.url === '/api/v1/players') return json(200, [{ id: 7, name: 'Bob', online: true }]);
    return json(200, {});
  });
  const hub = new CaptureHub();
  render(
    <AppProviders initialMe={{ username: 'admin', role, mustChangePassword: false }} hub={hub}>
      <MemoryRouter>
        <ChatPage />
      </MemoryRouter>
    </AppProviders>,
  );
  return { calls, hub };
}

describe('ChatPage', () => {
  it('loads history, subscribes to the chat group and appends live messages', async () => {
    const { hub } = setup();
    expect(await screen.findByText(/hello there/)).toBeInTheDocument();
    expect(hub.keys).toContain('chat');
    act(() => hub.push('Chat', msg(-1, 'Carol', 'live line', { at: '2026-01-01T10:05:00Z' })));
    expect(screen.getByText(/live line/)).toBeInTheDocument();
  });

  it('does not duplicate a live line that is already in the history', async () => {
    const { hub } = setup();
    await screen.findByText(/hello there/);
    act(() => hub.push('Chat', msg(-1, 'Alice', 'hello there', { at: '2026-01-01T10:00:01Z' })));
    expect(screen.getAllByText(/hello there/)).toHaveLength(1);
  });

  it('links a player message to the player page for muting', async () => {
    setup();
    const link = await screen.findByRole('link', { name: /Open Bob to mute/ });
    expect(link).toHaveAttribute('href', '/players/7');
    expect(screen.queryByRole('link', { name: /Open Alice/ })).not.toBeInTheDocument();
  });

  it('sends an everyone message as a broadcast', async () => {
    const user = userEvent.setup();
    const { calls } = setup('Admin', (c) => (c.method === 'POST' ? json(202, { delivered: 3 }) : (undefined as unknown as Response)));
    await screen.findByText(/hello there/);
    await user.click(screen.getByLabelText(/Show as on-screen broadcast banner/));
    await user.type(screen.getByLabelText('Message'), 'Restart at 22:00');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText(/Delivered to 3 player/)).toBeInTheDocument();
    const post = calls.find((c) => c.method === 'POST');
    expect(post?.url).toBe('/api/v1/chat');
    expect(JSON.parse(post?.body ?? '{}')).toMatchObject({ text: 'Restart at 22:00', channel: 'all', asBroadcast: true });
  });

  it('sends to a chosen player and shows server validation errors', async () => {
    const user = userEvent.setup();
    const { calls } = setup('Admin', (c) =>
      c.method === 'POST'
        ? json(400, { title: 'Validation failed', status: 400, code: 'ValidationFailed', errors: { text: ['Message is too long.'] } })
        : (undefined as unknown as Response),
    );
    await screen.findByText(/hello there/);
    await user.click(screen.getByRole('radio', { name: 'Player' }));
    await user.selectOptions(await screen.findByRole('combobox', { name: /^Player/ }), 'Bob');
    await user.type(screen.getByLabelText('Message'), 'psst');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText('Message is too long.')).toBeInTheDocument();
    expect(JSON.parse(calls.find((c) => c.method === 'POST')?.body ?? '{}')).toMatchObject({ channel: 'player', toPlayerId: 7 });
  });

  it('hides the send form from viewers', async () => {
    setup('Viewer');
    await screen.findByText(/hello there/);
    expect(screen.queryByRole('button', { name: 'Send' })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByText(/can read chat but not send/)).toBeInTheDocument());
  });
});
