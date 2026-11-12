import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import { AppRoutes } from '../../AppRoutes';
import type { AdminUserDto, ApiTokenDto, AuditEntryDto } from '../../generated/generated';
import { CaptureHub } from '../../test-utils/captureHub';
import { json, mockApi, type Call } from '../../test-utils/fakes';

const admin: AdminUserDto = { id: 1, username: 'admin', role: 'Admin', mustChange: false, createdAt: '2026-11-01T10:00:00Z' };
const viewer: AdminUserDto = { id: 2, username: 'viewer', role: 'Viewer', mustChange: true, createdAt: '2026-11-02T10:00:00Z' };
const token: ApiTokenDto = {
  id: 3,
  name: 'ci',
  role: 'Viewer',
  createdAt: '2026-11-02T11:00:00Z',
  lastUsedAt: null,
  revoked: false,
  owner: null,
};

const auditRows: AuditEntryDto[] = [
  {
    id: 5,
    at: '2026-11-03T09:00:00Z',
    actor: 'admin',
    action: 'user.create',
    target: '2',
    reason: null,
    data: { username: 'viewer', role: 'Viewer' },
    remoteIp: '127.0.0.1',
  },
  {
    id: 4,
    at: '2026-11-03T08:00:00Z',
    actor: 'admin',
    action: 'auth.login.success',
    target: 'admin',
    reason: null,
    data: { mustChange: 'false' },
    remoteIp: '127.0.0.1',
  },
];

let calls: Call[];

function setup(handler?: (c: Call) => Response | undefined) {
  calls = mockApi((c) => {
    const custom = handler?.(c);
    if (custom) return custom;
    if (c.url === '/api/v1/users' && c.method === 'GET') return json(200, [admin, viewer]);
    if (c.url === '/api/v1/users' && c.method === 'POST') {
      const body = JSON.parse(c.body ?? '{}') as { username: string; role: string };
      return json(201, { id: 9, username: body.username, role: body.role, mustChange: true, createdAt: '2026-11-03T10:00:00Z' });
    }
    if (c.url === '/api/v1/tokens' && c.method === 'GET') return json(200, [token]);
    if (c.url === '/api/v1/tokens' && c.method === 'POST') {
      const body = JSON.parse(c.body ?? '{}') as { name: string; role: string };
      return json(201, { id: 7, name: body.name, role: body.role, token: 'x4mp_plaintext-secret' });
    }
    if (c.url.startsWith('/api/v1/audit')) return json(200, auditRows);
    if (c.method === 'DELETE') return new Response(null, { status: 204 });
    return json(404, { code: 'NotFound', title: 'Not found.' });
  });
  const hub = new CaptureHub();
  render(
    <AppProviders initialMe={{ username: 'admin', role: 'Admin', mustChangePassword: false }} hub={hub}>
      <MemoryRouter initialEntries={['/access']}>
        <AppRoutes />
      </MemoryRouter>
    </AppProviders>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('Access page', () => {
  it('lists accounts and creates one with the entered role', async () => {
    const user = userEvent.setup();
    setup();
    expect(await screen.findByRole('heading', { name: 'Access', level: 1 })).toBeInTheDocument();

    const usersPanel = await screen.findByRole('table', { name: 'Admin accounts' });
    expect(within(usersPanel).getByText('admin')).toBeInTheDocument();
    expect(within(usersPanel).getByText('viewer')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: '+ Add user' }));
    await user.type(screen.getByLabelText('Username'), 'newbie');
    await user.type(screen.getByLabelText('Initial password'), 'long-enough-password');
    await user.selectOptions(screen.getByLabelText('Role'), 'ModEditor');
    await user.click(screen.getByRole('button', { name: 'Create user' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const post = calls.find((c) => c.method === 'POST' && c.url === '/api/v1/users');
    expect(post).toBeDefined();
    expect(JSON.parse(post!.body ?? '{}')).toMatchObject({ username: 'newbie', password: 'long-enough-password', role: 'ModEditor' });
    expect(screen.getByText('Created newbie (ModEditor).')).toBeInTheDocument();
    // The tab reloads the list after a change.
    expect(calls.filter((c) => c.method === 'GET' && c.url === '/api/v1/users')).toHaveLength(2);
  });

  it('creates a token, shows the secret once and revokes it', async () => {
    const user = userEvent.setup();
    setup();
    await user.click(await screen.findByRole('tab', { name: 'API tokens' }));

    const table = await screen.findByRole('table', { name: 'API tokens' });
    expect(within(table).getByText('ci')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: '+ Add token' }));
    await user.type(screen.getByLabelText('Name'), 'deploy');
    await user.click(screen.getByRole('button', { name: 'Create token' }));

    expect(await screen.findByText('x4mp_plaintext-secret')).toBeInTheDocument();
    expect(screen.getByText(/not shown again/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Done' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Revoke' }));
    await user.click(screen.getByRole('button', { name: 'Revoke token' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url === '/api/v1/tokens/3')).toBe(true));
    expect(screen.getByText('Revoked ci.')).toBeInTheDocument();
  });

  it('filters the audit log and reloads it with the chosen limit', async () => {
    const user = userEvent.setup();
    setup();
    await user.click(await screen.findByRole('tab', { name: 'Audit log' }));

    const table = await screen.findByRole('table', { name: 'Audit log' });
    expect(within(table).getByText('user.create')).toBeInTheDocument();
    expect(within(table).getByText('auth.login.success')).toBeInTheDocument();
    expect(calls.some((c) => c.url === '/api/v1/audit?limit=200')).toBe(true);

    await user.type(screen.getByLabelText('Filter audit rows'), 'auth.login');
    expect(within(table).queryByText('user.create')).not.toBeInTheDocument();
    expect(within(table).getByText('auth.login.success')).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Rows to load'), '50');
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/audit?limit=50')).toBe(true));
  });
});
