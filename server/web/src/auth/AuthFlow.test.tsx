import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppRoutes } from '../AppRoutes';
import { AuthProvider } from './AuthContext';

type Call = { url: string; method: string; csrf: string | null; body: string | null };

function mockApi(handler: (call: Call) => Response) {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init: RequestInit = {}) => {
      const headers = new Headers(init.headers);
      const call = {
        url,
        method: init.method ?? 'GET',
        csrf: headers.get('X-X4MP'),
        body: typeof init.body === 'string' ? init.body : null,
      };
      calls.push(call);
      return Promise.resolve(handler(call));
    }),
  );
  return calls;
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function renderApp(path = '/') {
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={[path]}>
        <AppRoutes />
      </MemoryRouter>
    </AuthProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('auth against the API', () => {
  it('asks /api/auth/me first and shows the app for a signed-in session', async () => {
    mockApi(() => json(200, { username: 'admin', role: 'Admin', mustChangePassword: false }));
    renderApp();
    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeInTheDocument();
  });

  it('logs in through the API with the CSRF header', async () => {
    let signedIn = false;
    const calls = mockApi((c) => {
      if (c.url === '/api/auth/login') {
        signedIn = true;
        return new Response(null, { status: 204 });
      }
      return signedIn ? json(200, { username: 'admin', role: 'Admin', mustChangePassword: false }) : json(401, {});
    });
    renderApp('/players');
    await userEvent.type(await screen.findByLabelText('Username'), 'admin');
    await userEvent.type(screen.getByLabelText('Password'), 'secret-password');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'Players' })).toBeInTheDocument();
    const login = calls.find((c) => c.url === '/api/auth/login');
    expect(login?.method).toBe('POST');
    expect(login?.csrf).toBe('1');
    expect(JSON.parse(login?.body ?? '{}')).toEqual({ username: 'admin', password: 'secret-password' });
  });

  it('shows an error for bad credentials', async () => {
    mockApi((c) =>
      c.url === '/api/auth/login'
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
      if (c.url === '/api/auth/change-password') {
        changed = true;
        return new Response(null, { status: 204 });
      }
      return json(200, { username: 'admin', role: 'Admin', mustChangePassword: !changed });
    });
    renderApp('/');
    expect(await screen.findByRole('heading', { name: 'Change password' })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Current password'), 'initial-password-x');
    await userEvent.type(screen.getByLabelText('New password'), 'a-brand-new-password');
    await userEvent.type(screen.getByLabelText('Confirm new password'), 'a-brand-new-password');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));

    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeInTheDocument();
    await waitFor(() => expect(calls.some((c) => c.url === '/api/auth/change-password')).toBe(true));
    const change = calls.find((c) => c.url === '/api/auth/change-password');
    expect(JSON.parse(change?.body ?? '{}')).toEqual({ current: 'initial-password-x', new: 'a-brand-new-password' });
  });
});
