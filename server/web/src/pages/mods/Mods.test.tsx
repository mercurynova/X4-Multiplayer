import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import { AppRoutes } from '../../AppRoutes';
import type {
  ExtensionDto,
  ExtensionReportDto,
  ModCatalogEntryDto,
  ModEntryDto,
  ModRefDto,
  ModsStateDto,
  ModViolationDto,
  PlayerExtensionsDto,
  PlayerModStatusDto,
  UnboundRejectionDto,
} from '../../generated/generated';
import { CaptureHub } from '../../test-utils/captureHub';
import { json, mockApi, type Call } from '../../test-utils/fakes';
import { applyPlayerReport } from './useMods';
import { previewImport } from './ImportDialog';

const ref = (id: string, name: string, extra: Partial<ModRefDto> = {}): ModRefDto => ({
  id,
  name,
  version: '1.4',
  haveVersion: '',
  nexusUrl: null,
  workshopId: 0,
  workshopUrl: null,
  workshopSteamUrl: null,
  notes: '',
  ...extra,
});

const entry = (id: string, name: string, extra: Partial<ModEntryDto> = {}): ModEntryDto => ({
  id,
  name,
  rule: 'Required',
  enabled: true,
  classOverride: 'Unknown',
  class: 'Sim',
  versionRule: 'Exact',
  version: '1.4',
  contentHash: null,
  nexusUrl: null,
  workshopId: 0,
  workshopUrl: null,
  workshopSteamUrl: null,
  notes: '',
  isLibrary: false,
  hasNativeDll: null,
  replacesBasegame: null,
  saveDependent: null,
  playersEnabled: 1,
  playersDisabled: 0,
  playersMissing: 1,
  ...extra,
});

const violation: ModViolationDto = {
  policyVersion: 7,
  install: [
    ref('ws_2458720435', 'Warehouse Fleets', {
      workshopId: 2458720435,
      workshopUrl: 'https://steamcommunity.com/sharedfiles/filedetails/?id=2458720435',
      workshopSteamUrl: 'steam://url/CommunityFilePage/2458720435',
      nexusUrl: 'https://www.nexusmods.com/x4foundations/mods/1234',
    }),
  ],
  enable: [],
  disable: [ref('ws_9000000001', 'Unlisted Gadgets', { version: '1.0' })],
  update: [],
};

const player = (playerId: number, name: string, extra: Partial<PlayerModStatusDto> = {}): PlayerModStatusDto => ({
  playerId,
  name,
  online: true,
  isAuthority: false,
  status: 'Matches',
  outcome: 'Admitted',
  reportedAt: '2026-10-02T10:00:00Z',
  reportPolicyVersion: 7,
  extensionCount: 4,
  violation: null,
  ...extra,
});

const state = (extra: Partial<ModsStateDto> = {}): ModsStateDto => ({
  policy: {
    version: 7,
    sourceMode: 'AdminList',
    unknownDefault: 'AllowClientOnly',
    enforcement: 'Strict',
    modListVisibility: 'AdminsOnly',
    entries: [
      entry('ws_2458720435', 'Warehouse Fleets', {
        workshopId: 2458720435,
        workshopUrl: 'https://steamcommunity.com/sharedfiles/filedetails/?id=2458720435',
        workshopSteamUrl: 'steam://url/CommunityFilePage/2458720435',
        nexusUrl: 'https://www.nexusmods.com/x4foundations/mods/1234',
      }),
      entry('sn_better_traders', 'Better Traders', { rule: 'Blocked', enabled: false }),
    ],
    updatedAt: '2026-10-02T09:00:00Z',
    updatedBy: 'admin:admin',
    authorityPlayerId: 1,
    authorityReportedAt: '2026-10-02T08:00:00Z',
  },
  players: [player(1, 'Alice', { isAuthority: true }), player(2, 'Bob', { status: 'Violates', outcome: 'Rejected', violation })],
  canEdit: true,
  playersHidden: false,
  saveRequirementsAvailable: false,
  ...extra,
});

const catalogEntry = (id: string, name: string, extra: Partial<ModCatalogEntryDto> = {}): ModCatalogEntryDto => ({
  id,
  name,
  nexusUrl: null,
  workshopId: 0,
  workshopUrl: null,
  workshopSteamUrl: null,
  classOverride: 'Unknown',
  notes: null,
  updatedAt: '2026-10-02T09:00:00Z',
  inPolicy: false,
  isLibrary: false,
  ...extra,
});

const unbound: UnboundRejectionDto = {
  keyId: 'abcdef012345',
  attemptedName: 'Mallory',
  at: '2026-10-02T10:30:00Z',
  policyVersion: 7,
  extensionCount: 3,
  violation,
};

interface Opts {
  state?: ModsStateDto;
  role?: string;
  catalog?: ModCatalogEntryDto[] | 'hidden';
  rejections?: UnboundRejectionDto[] | 'hidden';
  extensions?: PlayerExtensionsDto;
  handler?: (c: Call) => Response | undefined;
  path?: string;
}

let calls: Call[];
const hiddenProblem = () => json(403, { type: 'x', title: 'Forbidden.', status: 403, code: 'ModListHidden', detail: 'hidden' });

function setup(o: Opts = {}) {
  const st = o.state ?? state();
  calls = mockApi((c) => {
    const custom = o.handler?.(c);
    if (custom) return custom;
    if (c.url === '/api/v1/mods' && c.method === 'GET') return json(200, st);
    if (c.url === '/api/v1/mods/catalog') return o.catalog === 'hidden' ? hiddenProblem() : json(200, o.catalog ?? [catalogEntry('ws_1', 'Some Mod')]);
    if (c.url === '/api/v1/mods/rejections') return o.rejections === 'hidden' ? hiddenProblem() : json(200, o.rejections ?? []);
    if (/^\/api\/v1\/players\/\d+\/extensions$/.test(c.url)) return o.extensions ? json(200, o.extensions) : hiddenProblem();
    if (/^\/api\/v1\/players\/\d+$/.test(c.url)) return json(404, { code: 'NotFound', title: 'x' });
    return json(404, { code: 'NotFound', title: 'x' });
  });
  const hub = new CaptureHub();
  render(
    <AppProviders initialMe={{ username: 'u', role: o.role ?? 'Admin', mustChangePassword: false }} hub={hub}>
      <MemoryRouter initialEntries={[o.path ?? '/mods']}>
        <AppRoutes />
      </MemoryRouter>
    </AppProviders>,
  );
  return hub;
}

afterEach(() => vi.unstubAllGlobals());

describe('Mods page', () => {
  it('lets an admin edit: add, import, per-row controls', async () => {
    setup();
    expect(await screen.findByRole('heading', { name: 'Mods', level: 1 })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '+ Add mod' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Import from authority' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Edit Warehouse Fleets' })).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Enabled: Warehouse Fleets' })).toBeEnabled();
    expect(screen.getByRole('combobox', { name: 'Rule: Warehouse Fleets' })).toHaveValue('Required');
    expect(screen.getByRole('combobox', { name: /Enforcement/ })).toBeEnabled();
    expect(screen.getByText(/Changes apply to the next/)).toBeInTheDocument();
  });

  it('is read-only for a Viewer: no buttons, disabled policy selects', async () => {
    setup({ role: 'Viewer', state: state({ canEdit: false }) });
    await screen.findByRole('table', { name: 'Session mod list' });
    expect(screen.getByText(/Read-only/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '+ Add mod' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Import from authority' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Edit / })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Delete / })).not.toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: /^Rule:/ })).not.toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Enabled: Warehouse Fleets' })).toBeDisabled();
    expect(screen.getByRole('combobox', { name: /Enforcement/ })).toBeDisabled();
  });

  it('lets a ModEditor edit the list', async () => {
    setup({ role: 'ModEditor', state: state({ canEdit: true }) });
    expect(await screen.findByRole('button', { name: '+ Add mod' })).toBeInTheDocument();
  });

  it('handles ModListHidden: players, rejections and catalog explain instead of failing', async () => {
    setup({ role: 'Viewer', state: state({ canEdit: false, playersHidden: true, players: [] }), catalog: 'hidden', rejections: 'hidden' });
    await screen.findByRole('table', { name: 'Session mod list' });
    expect(screen.queryByRole('columnheader', { name: 'Players' })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByText(/hidden from your role/)).toHaveLength(3));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('opens links in a new tab', async () => {
    setup();
    const table = await screen.findByRole('table', { name: 'Session mod list' });
    const nexus = within(table).getByRole('link', { name: 'Nexus' });
    expect(nexus).toHaveAttribute('href', 'https://www.nexusmods.com/x4foundations/mods/1234');
    expect(nexus).toHaveAttribute('target', '_blank');
    expect(nexus).toHaveAttribute('rel', expect.stringContaining('noopener'));
    expect(within(table).getByRole('link', { name: 'Workshop' })).toHaveAttribute('target', '_blank');
  });

  it('changes the rule inline with a minimal PUT', async () => {
    setup({ handler: (c) => (c.method === 'PUT' ? json(200, entry('ws_2458720435', 'Warehouse Fleets', { rule: 'Allowed' })) : undefined) });
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Rule: Warehouse Fleets' }), 'Allowed');
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true));
    const put = calls.find((c) => c.method === 'PUT')!;
    expect(put.url).toBe('/api/v1/mods/entries/ws_2458720435');
    expect(JSON.parse(put.body ?? '{}')).toMatchObject({ rule: 'Allowed', enabled: null, nexusUrl: null });
  });

  it('shows per-field problems when an entry edit is refused', async () => {
    setup({
      handler: (c) =>
        c.method === 'PUT'
          ? json(400, {
              type: 'x',
              title: 'Validation failed',
              status: 400,
              code: 'ValidationFailed',
              errors: { NexusUrl: ['Use a link to a Nexus Mods page for X4.'], version: ['At most 64 characters.'] },
            })
          : undefined,
    });
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Warehouse Fleets' }));
    const dialog = screen.getByRole('dialog', { name: 'Edit Warehouse Fleets' });
    const nexus = within(dialog).getByLabelText(/Nexus URL/);
    await userEvent.clear(nexus);
    await userEvent.type(nexus, 'https://example.com/x');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await within(dialog).findByText('Use a link to a Nexus Mods page for X4.')).toBeInTheDocument();
    expect(within(dialog).getByText('At most 64 characters.')).toBeInTheDocument();
    expect(within(dialog).getByLabelText(/Nexus URL/)).toHaveAttribute('aria-invalid', 'true');
    // The dialog stays open so the admin can fix it.
    expect(dialog).toBeInTheDocument();
    const put = calls.find((c) => c.method === 'PUT')!;
    expect(JSON.parse(put.body ?? '{}')).toMatchObject({ nexusUrl: 'https://example.com/x', rule: 'Required', name: 'Warehouse Fleets' });
  });

  it('adds a mod from the dialog', async () => {
    setup({ handler: (c) => (c.method === 'PUT' ? json(201, entry('ws_1', 'Some Mod')) : undefined) });
    await userEvent.click(await screen.findByRole('button', { name: '+ Add mod' }));
    const dialog = screen.getByRole('dialog', { name: 'Add mod' });
    await userEvent.type(within(dialog).getByLabelText('Extension id'), 'ws_1');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add mod' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(calls.find((c) => c.method === 'PUT')?.url).toBe('/api/v1/mods/entries/ws_1');
  });

  it('shows rejections with the exact lists and links, including unknown-key attempts', async () => {
    setup({ rejections: [unbound] });
    const rejections = await waitFor(() => {
      const h = screen.getByRole('heading', { name: 'Recent rejections' });
      const el = h.parentElement as HTMLElement;
      expect(within(el).getByText('Mallory')).toBeInTheDocument();
      return el;
    });
    expect(within(rejections).getByText('unknown key abcdef012345')).toBeInTheDocument();
    const install = within(rejections).getAllByRole('region', { name: 'Install list' })[0]!;
    expect(within(install).getByText('Warehouse Fleets')).toBeInTheDocument();
    expect(within(install).getByRole('link', { name: 'Nexus' })).toHaveAttribute('href', 'https://www.nexusmods.com/x4foundations/mods/1234');
    expect(within(install).getByRole('link', { name: 'Steam' })).toHaveAttribute('href', 'steam://url/CommunityFilePage/2458720435');
    expect(within(rejections).getAllByRole('region', { name: 'Disable list' })[0]).toHaveTextContent('Unlisted Gadgets');
    // Bob is a known player whose latest report was rejected: listed too.
    expect(within(rejections).getByText('Bob')).toBeInTheDocument();
  });

  it('updates live: PlayerModsReported and ModPolicyChanged', async () => {
    const hub = setup();
    await screen.findByRole('table', { name: 'Session mod list' });
    expect(hub.keys).toContain('mods');
    expect(screen.queryByText('Carol')).not.toBeInTheDocument();
    act(() => hub.push('PlayerModsReported', player(3, 'Carol', { status: 'Violates', outcome: 'Warned', violation })));
    const players = screen.getByRole('heading', { name: 'Players', level: 2 }).parentElement as HTMLElement;
    expect(await within(players).findByText('Carol')).toBeInTheDocument();
    expect(within(players).getByText('Warned')).toBeInTheDocument();
    act(() => hub.push('PlayerModsReported', player(3, 'Carol', { status: 'Matches', outcome: 'Admitted' })));
    expect(within(players).queryByText('Warned')).not.toBeInTheDocument();
    act(() => hub.push('ModPolicyChanged', { ...state().policy, version: 8, enforcement: 'Warn' }));
    expect(await screen.findAllByText('policy v8')).not.toHaveLength(0);
    expect(screen.getByRole('combobox', { name: /Enforcement/ })).toHaveValue('Warn');
  });

  it('previews an import before changing anything, then confirms', async () => {
    const items = [
      { id: 'ego_dlc_split', name: 'Split Vendetta', enabled: true, effectiveClass: 'Dlc' },
      { id: 'ws_2458720435', name: 'Warehouse Fleets', enabled: true, effectiveClass: 'Sim' },
      { id: 'kuertee', name: 'UI Extensions', enabled: true, effectiveClass: 'ClientOnly' },
      { id: 'off', name: 'Off Mod', enabled: false, effectiveClass: 'Sim' },
    ] as ExtensionDto[];
    const latest = { items } as ExtensionReportDto;
    setup({
      extensions: { playerId: 1, name: 'Alice', latest, history: [] },
      handler: (c) => (c.url === '/api/v1/mods/import-from-authority' ? json(200, state().policy) : undefined),
    });
    await userEvent.click(await screen.findByRole('button', { name: 'Import from authority' }));
    const dialog = screen.getByRole('dialog', { name: 'Import from authority' });
    const table = await within(dialog).findByRole('table', { name: 'Import preview' });
    expect(within(table).getByText('Split Vendetta')).toBeInTheDocument();
    expect(within(table).getByText('UI Extensions')).toBeInTheDocument();
    expect(within(table).queryByText('Off Mod')).not.toBeInTheDocument();
    expect(within(table).getByText(/Already in the list/)).toBeInTheDocument();
    expect(calls.some((c) => c.url === '/api/v1/mods/import-from-authority')).toBe(false);
    await userEvent.click(within(dialog).getByRole('button', { name: /^Import \d+ mods$/ }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/v1/mods/import-from-authority')).toBe(true));
    expect(JSON.parse(calls.find((c) => c.url === '/api/v1/mods/import-from-authority')!.body ?? '{}')).toEqual({ merge: true });
  });
});

describe('mods state helpers', () => {
  it('upserts a player report by id', () => {
    let s = state();
    s = applyPlayerReport(s, player(2, 'Bob', { status: 'Matches', outcome: 'Admitted' }));
    expect(s.players).toHaveLength(2);
    expect(s.players.find((p) => p.playerId === 2)?.outcome).toBe('Admitted');
    s = applyPlayerReport(s, player(9, 'New'));
    expect(s.players).toHaveLength(3);
  });

  it('previews roles: DLC and sim Required, client-only Allowed, disabled and X4MP skipped', () => {
    const rows = previewImport(
      [
        { id: 'a', name: 'A', enabled: true, effectiveClass: 'Sim' },
        { id: 'b', name: 'B', enabled: true, effectiveClass: 'ClientOnly' },
        { id: 'c', name: 'C', enabled: false, effectiveClass: 'Sim' },
        { id: 'x4mp', name: 'X4MP', enabled: true, effectiveClass: 'Sim' },
      ] as ExtensionDto[],
      state().policy,
      true,
    );
    expect(rows.map((r) => [r.id, r.rule, r.action])).toEqual([
      ['a', 'Required', 'add'],
      ['b', 'Allowed', 'add'],
    ]);
  });
});

describe('Player detail Mods section', () => {
  const latest = {
    playerId: 2,
    at: '2026-10-02T10:00:00Z',
    outcome: 'Rejected',
    policyVersion: 7,
    extensionsHash: null,
    items: [{ id: 'ego_dlc_split', name: 'Split Vendetta', version: '900', source: 'Dlc', enabled: true, effectiveClass: 'Dlc', inPolicy: false }],
    violation,
    currentViolation: null,
  } as unknown as ExtensionReportDto;

  const detail = {
    player: { id: 2, name: 'Bob', notes: '', activeBan: false, muted: false, firstSeen: '2026-10-01T00:00:00Z', lastSeen: '2026-10-02T00:00:00Z', totalPlaytimeSeconds: 0, lastIp: null, mutedUntil: null },
    keyHash: 'abc',
    live: null,
    bans: [],
    history: [],
  };

  it('shows the latest report and updates when that player reports again', async () => {
    let extensions: PlayerExtensionsDto = {
      playerId: 2,
      name: 'Bob',
      latest,
      history: [{ at: latest.at, outcome: 'Rejected', policyVersion: 7, extensionsHash: null, extensionCount: 1, enabledCount: 1, violation }],
    };
    const hub = setup({
      path: '/players/2',
      handler: (c) => {
        if (/^\/api\/v1\/players\/2$/.test(c.url)) return json(200, detail);
        if (c.url === '/api/v1/players/2/extensions') return json(200, extensions);
        return undefined;
      },
    });
    const section = await screen.findByLabelText('Mods');
    expect((await within(section).findAllByText('Rejected')).length).toBeGreaterThan(0);
    expect(within(section).getAllByRole('region', { name: 'Install list' })[0]).toHaveTextContent('Warehouse Fleets');
    extensions = { ...extensions, latest: { ...latest, outcome: 'Admitted', violation: null }, history: [] };
    act(() => hub.push('PlayerModsReported', player(2, 'Bob')));
    expect(await within(section).findByText('Admitted')).toBeInTheDocument();
    expect(within(section).queryByRole('region', { name: 'Install list' })).not.toBeInTheDocument();
  });

  it('explains a hidden list', async () => {
    setup({
      path: '/players/2',
      handler: (c) => (/^\/api\/v1\/players\/2$/.test(c.url) ? json(200, detail) : undefined),
    });
    const section = await screen.findByLabelText('Mods');
    expect(await within(section).findByText(/hidden from your role/)).toBeInTheDocument();
  });
});
