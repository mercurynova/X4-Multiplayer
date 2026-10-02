import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { HubProvider } from '../hub/HubProvider';
import { FakeHubClient, json } from '../test-utils/fakes';
import { SessionStatus } from './SessionStatus';

const session = (state: string) => ({ id: 1, name: 'Run', state, saveName: null, saveSha256: null, startedAt: null, uptimeSeconds: 5, players: 0 });

afterEach(() => vi.unstubAllGlobals());

describe('SessionStatus', () => {
  it('shows stale state while the hub reconnects, then the refreshed state', async () => {
    let state = 'Running';
    let gate: Promise<void> = Promise.resolve();
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url === '/api/v1/server') return json(200, { name: 'Srv' });
        await gate;
        return json(200, session(state));
      }),
    );
    const hub = new FakeHubClient();
    render(
      <HubProvider client={hub}>
        <SessionStatus />
      </HubProvider>,
    );
    expect(await screen.findByText('RUNNING')).toBeInTheDocument();

    act(() => hub.setState('reconnecting'));
    expect(screen.queryByText('RUNNING')).not.toBeInTheDocument();
    expect(screen.getByText(/UNKNOWN/)).toBeInTheDocument();

    // Reconnected, but the refresh has not answered yet: still unknown.
    state = 'Paused';
    let release!: () => void;
    gate = new Promise<void>((r) => (release = r));
    act(() => hub.setState('connected'));
    expect(screen.getByText(/UNKNOWN/)).toBeInTheDocument();
    await act(async () => {
      release();
      await gate;
    });
    expect(await screen.findByText('PAUSED')).toBeInTheDocument();
    expect(screen.queryByText(/UNKNOWN/)).not.toBeInTheDocument();
  });
});
