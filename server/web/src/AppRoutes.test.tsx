import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from './AppProviders';
import { AppRoutes } from './AppRoutes';
import { screens } from './screens';
import { FakeHubClient, json, mockApi } from './test-utils/fakes';

function renderAt(path: string, authenticated: boolean, mustChangePassword = false, role = 'Admin', hub = new FakeHubClient()) {
  const me = authenticated ? { username: 'admin', role, mustChangePassword } : null;
  render(
    <AppProviders initialMe={me} hub={hub}>
      <MemoryRouter initialEntries={[path]}>
        <AppRoutes />
      </MemoryRouter>
    </AppProviders>,
  );
  return hub;
}

beforeEach(() => mockApi(() => json(200, {})));
afterEach(() => vi.unstubAllGlobals());

describe('routing', () => {
  it('renders the login page when unauthenticated', () => {
    renderAt('/players', false);
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('shows every screen in the nav when authenticated', () => {
    renderAt('/', true);
    const nav = screen.getByRole('navigation', { name: 'Main' });
    for (const s of screens) {
      expect(within(nav).getByRole('link', { name: s.title })).toBeInTheDocument();
    }
    expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeInTheDocument();
  });

  it('forces a password change before showing anything else', () => {
    renderAt('/players', true, true);
    expect(screen.getByRole('heading', { name: 'Change password' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument();
  });

  it('renders the 404 page for unknown routes', () => {
    renderAt('/nope', true);
    expect(screen.getByRole('heading', { name: 'Page not found' })).toBeInTheDocument();
  });

  it('serves detail routes as placeholders', () => {
    renderAt('/players/12', true);
    expect(screen.getByRole('heading', { name: 'Player' })).toBeInTheDocument();
  });

  it('shows the signed-in role in the user menu', () => {
    renderAt('/', true, false, 'Viewer');
    expect(screen.getByText('Viewer')).toBeInTheDocument();
  });
});

describe('frame', () => {
  it('shows the hub connection state and follows changes', () => {
    const hub = renderAt('/', true);
    expect(screen.getByRole('status', { name: 'Live updates: live' })).toBeInTheDocument();
    act(() => hub.setState('reconnecting'));
    expect(screen.getByRole('status', { name: 'Live updates: reconnecting' })).toBeInTheDocument();
  });

  it('shows a server alert as a persistent banner and a plain one as a toast', async () => {
    const hub = renderAt('/', true);
    act(() => {
      hub.emit('Alert', { severity: 'error', message: 'Authority FPS below 15 for 30 s', persistent: true, id: 'fps' });
      hub.emit('Alert', { severity: 'info', message: 'Save finished' });
    });
    expect(screen.getByRole('alert')).toHaveTextContent('Authority FPS below 15');
    expect(screen.getByText('Save finished')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss alert' }));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
