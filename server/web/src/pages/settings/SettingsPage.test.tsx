import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import type { SettingSchemaDto, SettingsDto } from '../../generated/generated';
import { CaptureHub } from '../../test-utils/captureHub';
import { json, mockApi, type Call } from '../../test-utils/fakes';
import { SettingsPage } from './SettingsPage';

afterEach(() => vi.unstubAllGlobals());

const def = (o: Partial<SettingSchemaDto> & Pick<SettingSchemaDto, 'key' | 'name' | 'type'>): SettingSchemaDto => ({
  section: o.key.split('.')[0]!,
  category: 'x',
  description: `About ${o.name}.`,
  requiresRestart: false,
  min: null,
  max: null,
  maxLength: null,
  values: null,
  secret: false,
  pushToNodes: false,
  default: null,
  ...o,
});

const schema: SettingSchemaDto[] = [
  def({ key: 'Session.MaxPlayers', name: 'Max players', type: 'int', min: 1, max: 24, default: 8 }),
  def({ key: 'Session.Motd', name: 'Message of the day', type: 'string', maxLength: 200, default: '' }),
  def({ key: 'Session.JoinPassword', name: 'Join password', type: 'string', secret: true }),
  def({ key: 'Session.Open', name: 'Open to join', type: 'bool', default: true }),
  def({ key: 'Mods.ModListVisibility', name: 'Mod list visibility', type: 'enum', values: ['AdminsOnly', 'AdminsAndViewers', 'AllPlayers'], default: 'AdminsOnly' }),
  def({ key: 'Teams.Names', name: 'Team names', type: 'stringList', default: [] }),
  def({ key: 'Net.TcpPort', name: 'TCP port', type: 'int', requiresRestart: true, default: 47780 }),
];

const values = (over: Record<string, unknown> = {}, overrides: string[] = []): SettingsDto => ({
  sections: {
    Session: { MaxPlayers: 8, Motd: 'hi', JoinPassword: '********', Open: true, ...over },
    Mods: { ModListVisibility: 'AdminsOnly' },
    Teams: { Names: ['a', 'b'] },
    Net: { TcpPort: 47780 },
  },
  overrides,
});

function setup(patch?: (c: Call) => Response, role = 'Admin', current = values()) {
  const calls = mockApi((c) => {
    if (c.url === '/api/v1/settings/schema') return json(200, { settings: schema });
    if (c.url === '/api/v1/settings' && c.method === 'GET') return json(200, current);
    if (c.url === '/api/v1/settings' && c.method === 'PATCH' && patch) return patch(c);
    return json(200, {});
  });
  const hub = new CaptureHub();
  render(
    <AppProviders initialMe={{ username: 'admin', role, mustChangePassword: false }} hub={hub}>
      <SettingsPage />
    </AppProviders>,
  );
  return { calls, hub };
}

const problem = (errors: Record<string, string[]>, errorCodes: Record<string, string> = {}) =>
  json(400, { title: 'Validation failed', detail: 'One or more settings were rejected; nothing was changed.', status: 400, code: 'ValidationFailed', errors, errorCodes });

describe('SettingsPage', () => {
  it('builds typed inputs per section from the schema', async () => {
    setup();
    expect(await screen.findByRole('group', { name: 'Session' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Mods' })).toBeInTheDocument();
    expect(screen.getByLabelText('Max players')).toHaveValue('8');
    expect(screen.getByLabelText('Open to join')).toBeChecked();
    expect(screen.getByLabelText('Mod list visibility')).toHaveValue('AdminsOnly');
    expect(screen.getByLabelText('Team names')).toHaveValue('a\nb');
    expect(screen.getByLabelText('Join password')).toHaveAttribute('type', 'password');
    expect(screen.getByLabelText('Join password')).toHaveValue('');
  });

  it('marks Boot settings read-only with a restart-required badge and notice', async () => {
    setup();
    const port = await screen.findByLabelText('TCP port');
    expect(port).toBeDisabled();
    expect(screen.getAllByText('Boot - restart required').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Live').length).toBeGreaterThan(0);
    expect(screen.getByText(/edit appsettings.json and restart/)).toBeInTheDocument();
  });

  it('sends only the changed keys, converted to their types', async () => {
    const user = userEvent.setup();
    const { calls } = setup(() => json(200, values({ MaxPlayers: 2 }, ['Session.MaxPlayers'])));
    const input = await screen.findByLabelText('Max players');
    await user.clear(input);
    await user.type(input, '2');
    await user.click(screen.getByLabelText('Open to join'));
    await user.click(screen.getByRole('button', { name: 'Save changes (2)' }));
    expect(await screen.findByText('Saved 2 setting(s).')).toBeInTheDocument();
    const patch = calls.find((c) => c.method === 'PATCH');
    expect(JSON.parse(patch?.body ?? '{}')).toEqual({ 'Session.MaxPlayers': 2, 'Session.Open': false });
    expect(screen.getByRole('button', { name: 'Reset to default' })).toBeInTheDocument();
  });

  it('rejects text that is not a number before calling the server', async () => {
    const user = userEvent.setup();
    const { calls } = setup();
    const input = await screen.findByLabelText('Max players');
    await user.clear(input);
    await user.type(input, 'lots');
    await user.click(screen.getByRole('button', { name: /Save changes/ }));
    expect(await screen.findByText('Enter a whole number.')).toBeInTheDocument();
    expect(calls.some((c) => c.method === 'PATCH')).toBe(false);
  });

  it('shows the 400 error under the right key and keeps the edit', async () => {
    const user = userEvent.setup();
    setup(() => problem({ 'Session.MaxPlayers': ['Session.MaxPlayers must be between 1 and 24.'] }, { 'Session.MaxPlayers': 'OutOfRange' }));
    const input = await screen.findByLabelText('Max players');
    await user.clear(input);
    await user.type(input, '99');
    await user.click(screen.getByRole('button', { name: /Save changes/ }));
    expect(await screen.findByText('Session.MaxPlayers must be between 1 and 24.')).toBeInTheDocument();
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveValue('99');
    expect(screen.getByLabelText('Message of the day')).not.toHaveAttribute('aria-invalid', 'true');
  });

  it('shows the restart-required notice when the server refuses a Boot key', async () => {
    const user = userEvent.setup();
    setup(() => problem({ 'Session.MaxPlayers': ['is a boot setting: edit appsettings.json and restart the server (restart required).'] }, { 'Session.MaxPlayers': 'RestartRequired' }));
    const input = await screen.findByLabelText('Max players');
    await user.clear(input);
    await user.type(input, '3');
    await user.click(screen.getByRole('button', { name: /Save changes/ }));
    expect(await screen.findByText(/Restart required: Session.MaxPlayers/)).toBeInTheDocument();
  });

  it('resets an overridden key to its default with a null PATCH', async () => {
    const user = userEvent.setup();
    const { calls } = setup(() => json(200, values()), 'Admin', values({ MaxPlayers: 12 }, ['Session.MaxPlayers']));
    await user.click(await screen.findByRole('button', { name: 'Reset to default' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true));
    expect(JSON.parse(calls.find((c) => c.method === 'PATCH')?.body ?? '{}')).toEqual({ 'Session.MaxPlayers': null });
    expect(await screen.findByText(/Max players reset to its default/)).toBeInTheDocument();
    expect(screen.getByLabelText('Max players')).toHaveValue('8');
  });

  it('refreshes values on SettingsChanged without losing unsaved edits', async () => {
    const user = userEvent.setup();
    const { hub } = setup();
    const motd = await screen.findByLabelText('Message of the day');
    await user.type(motd, '!!');
    act(() => hub.emit('SettingsChanged', values({ MaxPlayers: 5 }, ['Session.MaxPlayers'])));
    await waitFor(() => expect(screen.getByLabelText('Max players')).toHaveValue('5'));
    expect(motd).toHaveValue('hi!!');
  });

  it('is read-only for viewers', async () => {
    setup(undefined, 'Viewer');
    expect(await screen.findByLabelText('Max players')).toBeDisabled();
    expect(screen.queryByRole('button', { name: /Save changes/ })).not.toBeInTheDocument();
  });
});
