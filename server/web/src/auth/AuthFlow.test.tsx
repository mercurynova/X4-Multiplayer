import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../AppProviders';
import { AppRoutes } from '../AppRoutes';
import { FakeHubClient, json, mockApi } from '../test-utils/fakes';

const admin = (mustChangePassword = false) => ({ username: 'admin', role: 'Admin', mustChangePassword });

function renderApp(path = '/') {
  return render(
    <AppProviders hub={new FakeHubClient()}>
      <MemoryRouter initialEntries={[path]}>
        <AppRoutes />
      </MemoryRouter>
    </AppProviders>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('auth against the API', () => {
  it('asks /api/v1/auth/me first and shows the app for a signed-in session', async () => {
    mockApi(() => json(200, admin()));
    renderApp();
    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeInTheDocument();
  });

  it('logs in through the API with the CSRF header', async () => {
    let signedIn = false;
    const calls = mockApi((c) => {
      if (c.url === '/api/v1/auth/login') {
        signedIn = true;
        return new Response(null, { status: 204 });
      }
      return signedIn ? json(200, admin()) : json(401, {});
    });
    renderApp('/players');
    await userEvent.type(await screen.findByLabelText('Username'), 'admin');
    await userEvent.type(screen.getByLabelText('Password'), 'secret-password');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'Players' })).toBeInTheDocument();
    const login = calls.find((c) => c.url === '/api/v1/auth/login');
    expect(login?.method).toBe('POST');
    expect(login?.csrf).toBe('1');
    expect(JSON.parse(login?.body ?? '{}')).toEqual({ username: 'admin', password: 'secret-password' });
  });

  it('shows an error for bad credentials', async () => {
    mockApi((c) =>
      c.url === '/api/v1/auth/login'
        ? json(401, { title: 'Invalid username or password.', status: 401, code: 'InvalidCredentials' })
        : json(401, {}),
    );
    renderApp('/');
    await userEvent.type(await screen.findByLabelText('Username'), 'admin');
    await userEvent.type(screen.getByLabelText('Password'), 'nope');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid username or password.');
  });

  it('forces a password change and then continues into the app', async () => {
    let changed = false;
    const calls = mockApi((c) => {
      if (c.url === '/api/v1/auth/change-password') {
        changed = true;
        return new Response(null, { status: 204 });
      }
      return json(200, admin(!changed));
    });
    renderApp('/');
    expect(await screen.findByRole('heading', { name: 'Change password' })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Current password'), 'initial-password-x');
    await userEvent.type(screen.getByLabelText('New password'), 'a-brand-new-password');
    await userEvent.type(screen.getByLabelText('Confirm new password'), 'a-brand-new-password');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));

    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeInTheDocument();
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/auth/change-password')).toBe(true));
    const change = calls.find((c) => c.url === '/api/v1/auth/change-password');
    expect(JSON.parse(change?.body ?? '{}')).toEqual({ current: 'initial-password-x', new: 'a-brand-new-password' });
  });

  it('shows per-field errors from a 400 on the password change', async () => {
    mockApi((c) =>
      c.url === '/api/v1/auth/change-password'
        ? json(400, { status: 400, title: 'Validation failed', code: 'ValidationFailed', errors: { Current: ['Current password is wrong.'] } })
        : json(200, admin(true)),
    );
    renderApp('/');
    await userEvent.type(await screen.findByLabelText('Current password'), 'wrong-password-1');
    await userEvent.type(screen.getByLabelText('New password'), 'a-brand-new-password');
    await userEvent.type(screen.getByLabelText('Confirm new password'), 'a-brand-new-password');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByText('Current password is wrong.')).toBeInTheDocument();
    expect(screen.getByLabelText('Current password', { exact: false })).toHaveAttribute('aria-invalid', 'true');
  });

  it('logs out through the user menu and lands on the login page', async () => {
    let signedOut = false;
    const calls = mockApi((c) => {
      if (c.url === '/api/v1/auth/logout') {
        signedOut = true;
        return new Response(null, { status: 204 });
      }
      return signedOut ? json(401, {}) : json(200, admin());
    });
    renderApp('/');
    await screen.findByRole('heading', { name: 'Dashboard' });
    await userEvent.click(screen.getByText('admin'));
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    const logout = calls.find((c) => c.url === '/api/v1/auth/logout');
    expect(logout?.csrf).toBe('1');
  });

  it('returns to the login page with a notice when the session expires mid-use', async () => {
    let expired = false;
    mockApi((c) => {
      if (c.url === '/api/v1/auth/me') return expired ? json(401, {}) : json(200, admin());
      if (c.url === '/api/v1/players') return json(401, { status: 401, title: 'Unauthorized', code: 'Unauthorized' });
      return json(200, {});
    });
    renderApp('/');
    await screen.findByRole('heading', { name: 'Dashboard' });
    expired = true;
    // Any page's API call answering 401 ends the session.
    const { api } = await import('../api/http');
    await expect(api.get('/api/v1/players')).rejects.toMatchObject({ status: 401 });

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Your session has expired');
  });
});
