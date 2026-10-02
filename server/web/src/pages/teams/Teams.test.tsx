import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import { AppRoutes } from '../../AppRoutes';
import type { TeamDto, TeamMemberDto, TeamPresetPreviewDto, TeamsStateDto } from '../../generated/generated';
import { CaptureHub } from '../../test-utils/captureHub';
import { json, mockApi, type Call } from '../../test-utils/fakes';
import { applyTeamsPush, EMPTY_STATE, withPendingMoves } from './teamsState';

const team = (id: number, name: string, extra: Partial<TeamDto> = {}): TeamDto => ({
  id,
  name,
  color: '#3FA7FF',
  factionSlot: id,
  leaderPlayerId: null,
  locked: false,
  maxMembers: null,
  hasPassword: false,
  memberCount: 0,
  ...extra,
});

const member = (playerId: number, name: string, teamId: number | null, extra: Partial<TeamMemberDto> = {}): TeamMemberDto => ({
  playerId,
  name,
  teamId,
  role: 'Member',
  online: true,
  isAuthority: false,
  assignedBy: teamId === null ? '' : 'auto',
  since: null,
  ...extra,
});

const state = (extra: Partial<TeamsStateDto> = {}): TeamsStateDto => ({
  ...EMPTY_STATE,
  teams: [team(1, 'Red', { memberCount: 2, leaderPlayerId: 1 }), team(2, 'Blue', { memberCount: 1 }), team(3, 'Green')],
  members: [member(1, 'Alice', 1, { isAuthority: true, role: 'Leader' }), member(2, 'Bob', 1), member(3, 'Dan', 2)],
  unassigned: [member(4, 'Carol', null)],
  relations: { version: 2, entries: [{ teamA: 1, teamB: 2, relation: 'Hostile' }], defaultRelation: 'Neutral' },
  sessionPhase: 'Running',
  ...extra,
});

let calls: Call[];

function setup(initial: TeamsStateDto, handler: (c: Call) => Response = () => json(404, { code: 'NotFound', title: 'x' }), role = 'Admin') {
  calls = mockApi((c) => (c.url === '/api/v1/teams' && c.method === 'GET' ? json(200, initial) : handler(c)));
  const hub = new CaptureHub();
  render(
    <AppProviders initialMe={{ username: 'admin', role, mustChangePassword: false }} hub={hub}>
      <MemoryRouter initialEntries={['/teams']}>
        <AppRoutes />
      </MemoryRouter>
    </AppProviders>,
  );
  return hub;
}

const card = (name: string) => screen.getByRole('article', { name: name === 'Unassigned' ? 'Unassigned' : `Team ${name}` });
const dataTransfer = () => ({ setData: vi.fn(), getData: vi.fn(() => ''), effectAllowed: 'none', dropEffect: 'none' });
const sentTo = (url: string, method: string) => calls.filter((c) => c.url === url && c.method === method);

afterEach(() => vi.unstubAllGlobals());

describe('teams state', () => {
  it('applies each hub push idempotently', () => {
    let s = state();
    s = applyTeamsPush(s, 'TeamUpserted', team(2, 'Navy', { memberCount: 1 }));
    expect(s.teams.map((t) => t.name)).toEqual(['Red', 'Navy', 'Green']);
    s = applyTeamsPush(s, 'TeamMemberChanged', member(2, 'Bob', 3));
    s = applyTeamsPush(s, 'TeamMemberChanged', member(2, 'Bob', 3));
    expect(s.members.filter((m) => m.playerId === 2)).toHaveLength(1);
    expect(s.members.find((m) => m.playerId === 2)?.teamId).toBe(3);
    s = applyTeamsPush(s, 'TeamMemberChanged', member(3, 'Dan', null));
    expect(s.unassigned.map((m) => m.name)).toEqual(['Carol', 'Dan']);
    expect(s.members.some((m) => m.playerId === 3)).toBe(false);
    s = applyTeamsPush(s, 'PlayerAwaitingTeam', member(9, 'Newbie', null));
    expect(s.unassigned.some((m) => m.name === 'Newbie')).toBe(true);
    s = applyTeamsPush(s, 'PlayerAwaitingTeam', member(2, 'Bob', null)); // already placed: ignored
    expect(s.unassigned.some((m) => m.name === 'Bob')).toBe(false);
    s = applyTeamsPush(s, 'TeamDeleted', 3);
    expect(s.teams.some((t) => t.id === 3)).toBe(false);
    s = applyTeamsPush(s, 'TeamRelationsChanged', { version: 9, entries: [], defaultRelation: 'Hostile' });
    expect(s.relations.version).toBe(9);
    expect(applyTeamsPush(s, 'TeamsReset', state()).teams).toHaveLength(3);
  });

  it('shows pending moves on top and fixes the member counts', () => {
    const s = withPendingMoves(state(), new Map([[2, 2]]));
    expect(s.members.find((m) => m.playerId === 2)?.teamId).toBe(2);
    expect(s.teams.map((t) => t.memberCount)).toEqual([1, 2, 0]);
    expect(withPendingMoves(state(), new Map())).toEqual(state());
  });
});

describe('Teams & Factions page', () => {
  it('shows the teams with their members, the unassigned badge and the relation grid', async () => {
    const hub = setup(state());
    await screen.findByRole('heading', { name: 'Teams & Factions' });
    await screen.findByRole('article', { name: 'Team Red' });
    expect(within(card('Red')).getByText('Alice')).toBeInTheDocument();
    expect(within(card('Red')).getByText('authority')).toBeInTheDocument();
    expect(within(card('Red')).getByText('2/∞ members')).toBeInTheDocument();
    expect(within(card('Unassigned')).getByText('Carol')).toBeInTheDocument();
    expect(within(card('Unassigned')).getByText(/waiting/)).toBeInTheDocument();
    expect(screen.getByText('1 unassigned')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Red and Blue: Hostile' })).toHaveTextContent('HOSTILE');
    expect(screen.getByRole('button', { name: 'Blue and Red: Hostile' })).toBeInTheDocument(); // symmetric
    expect(screen.getByRole('button', { name: 'Red and Green: Neutral' })).toBeInTheDocument();
    expect(hub.keys).toContain('teams');
  });

  it('is read-only for a Viewer', async () => {
    setup(state(), undefined, 'Viewer');
    await screen.findByRole('article', { name: 'Team Red' });
    expect(screen.queryByRole('button', { name: '+ New team' })).not.toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: /Move Bob to/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Edit Red' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Apply relations/ })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Red and Blue: Hostile').tagName).toBe('SPAN');
    expect(screen.getByRole('button', { name: 'Free-for-all' })).toBeDisabled();
  });

  it('moves a player by drag and drop, shows it at once and keeps it after the server answers', async () => {
    let answer!: (r: Response) => void;
    setup(state(), (c) =>
      c.url === '/api/v1/teams/members/2' ? (undefined as unknown as Response) : json(404, {}),
    );
    // hold the PUT open so the optimistic state is visible
    const original = globalThis.fetch as unknown as (url: string, init?: RequestInit) => Promise<Response>;
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string, init?: RequestInit) =>
        url === '/api/v1/teams/members/2'
          ? new Promise<Response>((resolve) => {
              calls.push({ url, method: init?.method ?? 'GET', csrf: null, body: typeof init?.body === 'string' ? init.body : null });
              answer = resolve;
            })
          : original(url, init),
      ),
    );
    await screen.findByRole('article', { name: 'Team Red' });
    const bob = within(card('Red')).getByText('Bob').closest('li')!;
    expect(bob).toHaveAttribute('draggable', 'true');
    const dt = dataTransfer();
    fireEvent.dragStart(bob, { dataTransfer: dt });
    fireEvent.dragEnter(card('Blue'), { dataTransfer: dt });
    fireEvent.dragOver(card('Blue'), { dataTransfer: dt });
    expect(card('Blue')).toHaveClass('drop-over');
    fireEvent.drop(card('Blue'), { dataTransfer: dt });

    await waitFor(() => expect(within(card('Blue')).getByText('Bob')).toBeInTheDocument()); // optimistic
    expect(within(card('Red')).queryByText('Bob')).not.toBeInTheDocument();
    expect(within(card('Blue')).getByText('2/∞ members')).toBeInTheDocument();
    await waitFor(() => expect(sentTo('/api/v1/teams/members/2', 'PUT')).toHaveLength(1));
    expect(JSON.parse(sentTo('/api/v1/teams/members/2', 'PUT')[0]!.body!)).toMatchObject({ teamId: 2 });
    await act(async () => answer(json(200, member(2, 'Bob', 2, { assignedBy: 'admin:admin' }))));
    expect(within(card('Blue')).getByText('Bob')).toBeInTheDocument();
  });

  it('rolls a refused move back and says why', async () => {
    setup(state(), (c) =>
      c.url === '/api/v1/teams/members/1'
        ? json(409, { type: 'urn:x4mp:problem:SessionRunningRestricted', title: 'Conflict.', status: 409, code: 'SessionRunningRestricted', detail: "The authority's player cannot change team while the session is running." })
        : json(404, {}),
    );
    await screen.findByRole('article', { name: 'Team Red' });
    const alice = within(card('Red')).getByText('Alice').closest('li')!;
    const dt = dataTransfer();
    fireEvent.dragStart(alice, { dataTransfer: dt });
    fireEvent.dragOver(card('Green'), { dataTransfer: dt });
    fireEvent.drop(card('Green'), { dataTransfer: dt });
    expect(await screen.findByText(/cannot change team while the session is running/)).toBeInTheDocument();
    await waitFor(() => expect(within(card('Red')).getByText('Alice')).toBeInTheDocument());
    expect(within(card('Green')).queryByText('Alice')).not.toBeInTheDocument();
  });

  it('moves a player from the keyboard with the Move-to menu, also back to Unassigned', async () => {
    const user = userEvent.setup();
    setup(state(), (c) => (c.url.startsWith('/api/v1/teams/members/') ? json(200, member(2, 'Bob', 3)) : json(404, {})));
    await screen.findByRole('article', { name: 'Team Red' });
    const menu = within(card('Red')).getByRole('combobox', { name: 'Move Bob to' });
    expect(within(menu).queryByRole('option', { name: 'Red' })).not.toBeInTheDocument(); // not its own team
    await user.selectOptions(menu, 'Green');
    await waitFor(() => expect(within(card('Green')).getByText('Bob')).toBeInTheDocument());
    expect(JSON.parse(sentTo('/api/v1/teams/members/2', 'PUT')[0]!.body!)).toMatchObject({ teamId: 3 });

    await user.selectOptions(within(card('Blue')).getByRole('combobox', { name: 'Move Dan to' }), 'Unassigned');
    await waitFor(() => expect(sentTo('/api/v1/teams/members/3', 'PUT')).toHaveLength(1));
    expect(JSON.parse(sentTo('/api/v1/teams/members/3', 'PUT')[0]!.body!)).toMatchObject({ teamId: null });
    // a player waiting in Unassigned can be placed the same way
    await user.selectOptions(within(card('Unassigned')).getByRole('combobox', { name: 'Move Carol to' }), 'Blue');
    await waitFor(() => expect(JSON.parse(sentTo('/api/v1/teams/members/4', 'PUT')[0]!.body!)).toMatchObject({ teamId: 2 }));
  });

  it('stages relation edits and applies them as one request', async () => {
    const user = userEvent.setup();
    setup(state(), (c) =>
      c.url === '/api/v1/teams/relations' && c.method === 'PUT' ? json(200, { version: 3, entries: [], defaultRelation: 'Neutral' }) : json(404, {}),
    );
    await screen.findByRole('article', { name: 'Team Red' });
    const apply = screen.getByRole('button', { name: /Apply relations/ });
    expect(apply).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Red and Blue: Hostile' })); // Hostile -> Allied
    expect(screen.getByRole('button', { name: 'Blue and Red: Allied (changed)' })).toHaveTextContent('ALLIED');
    await user.click(screen.getByRole('button', { name: 'Red and Green: Neutral' })); // Neutral -> Hostile
    await user.click(screen.getByRole('button', { name: 'Red and Green: Hostile (changed)' })); // -> Allied
    await user.click(screen.getByRole('button', { name: 'Red and Green: Allied (changed)' })); // -> Neutral = the server's value again
    expect(screen.getByRole('button', { name: 'Red and Green: Neutral' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply relations (1)' })).toBeEnabled();
    expect(sentTo('/api/v1/teams/relations', 'PUT')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Discard' }));
    expect(screen.getByRole('button', { name: 'Red and Blue: Hostile' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Red and Blue: Hostile' }));
    await user.click(screen.getByRole('button', { name: 'Apply relations (1)' }));
    await waitFor(() => expect(sentTo('/api/v1/teams/relations', 'PUT')).toHaveLength(1));
    expect(JSON.parse(sentTo('/api/v1/teams/relations', 'PUT')[0]!.body!)).toEqual({
      entries: [{ teamA: 1, teamB: 2, relation: 'Allied' }],
    });
  });

  const preview = (extra: Partial<TeamPresetPreviewDto> = {}): TeamPresetPreviewDto => ({
    preset: 'FreeForAll',
    running: true,
    requiresConfirm: true,
    blocked: null,
    blockedDetail: null,
    teams: [team(1, 'Alice', { memberCount: 1 }), team(2, 'Bob', { memberCount: 1 })],
    members: [],
    playersMoved: 2,
    teamsRemoved: 3,
    autoAssign: 'NewTeamPerPlayer',
    relation: 'Hostile',
    ...extra,
  });

  it('asks for a confirmation with a preview before a preset changes anything', async () => {
    const user = userEvent.setup();
    setup(state(), (c) => {
      if (c.url === '/api/v1/teams/preset/FreeForAll/preview') return json(200, preview());
      if (c.url === '/api/v1/teams/preset' && c.method === 'POST') return json(200, state());
      return json(404, {});
    });
    await screen.findByRole('article', { name: 'Team Red' });
    await user.click(screen.getByRole('button', { name: 'Free-for-all' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Apply preset: Free-for-all' });
    expect(within(dialog).getByText(/2 teams: Alice \(1\), Bob \(1\)/)).toBeInTheDocument();
    expect(within(dialog).getByText(/2 players change team; 3 existing teams are replaced/)).toBeInTheDocument();
    expect(within(dialog).getByText(/every pair of teams becomes Hostile/)).toBeInTheDocument();
    expect(within(dialog).getByText(/session is running/)).toBeInTheDocument();
    expect(sentTo('/api/v1/teams/preset', 'POST')).toHaveLength(0); // nothing changed yet

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    expect(sentTo('/api/v1/teams/preset', 'POST')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Free-for-all' }));
    await user.click(await screen.findByRole('button', { name: 'Apply and move players' }));
    await waitFor(() => expect(sentTo('/api/v1/teams/preset', 'POST')).toHaveLength(1));
    expect(JSON.parse(sentTo('/api/v1/teams/preset', 'POST')[0]!.body!)).toEqual({ preset: 'FreeForAll', confirm: true });
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });

  it('cannot apply a preset the server says is blocked', async () => {
    const user = userEvent.setup();
    setup(state(), (c) =>
      c.url === '/api/v1/teams/preset/TwoTeams/preview'
        ? json(200, preview({ preset: 'TwoTeams', blocked: 'SessionRunningRestricted', blockedDetail: "the authority's player cannot change team while the session is running" }))
        : json(404, {}),
    );
    await screen.findByRole('article', { name: 'Team Red' });
    await user.click(screen.getByRole('button', { name: 'Two teams (versus)' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByRole('alert')).toHaveTextContent(/Not possible now.*authority/);
    expect(within(dialog).getByRole('button', { name: 'Apply and move players' })).toBeDisabled();
  });

  it('follows the hub: moves, new teams, relations and a waiting player appear without a reload', async () => {
    const hub = setup(state());
    await screen.findByRole('article', { name: 'Team Red' });
    act(() => hub.push('TeamMemberChanged', member(2, 'Bob', 2)));
    expect(within(card('Blue')).getByText('Bob')).toBeInTheDocument();
    act(() => hub.push('TeamUpserted', team(4, 'Gold', { memberCount: 0 })));
    expect(screen.getByRole('article', { name: 'Team Gold' })).toBeInTheDocument();
    act(() => hub.push('TeamRelationsChanged', { version: 5, entries: [{ teamA: 1, teamB: 2, relation: 'Allied' }], defaultRelation: 'Neutral' }));
    expect(screen.getByRole('button', { name: 'Red and Blue: Allied' })).toBeInTheDocument();
    act(() => hub.push('PlayerAwaitingTeam', member(7, 'Zed', null)));
    expect(within(card('Unassigned')).getByText('Zed')).toBeInTheDocument();
    expect(screen.getByText('2 unassigned')).toBeInTheDocument();
    act(() => hub.push('TeamDeleted', 3));
    expect(screen.queryByRole('article', { name: 'Team Green' })).not.toBeInTheDocument();
    act(() => hub.push('$snapshot', state({ teams: [team(1, 'Only')], members: [], unassigned: [] })));
    expect(screen.getByRole('article', { name: 'Team Only' })).toBeInTheDocument();
    expect(screen.queryByRole('article', { name: 'Team Red' })).not.toBeInTheDocument();
  });

  it('creates a team and shows a server validation error inline', async () => {
    const user = userEvent.setup();
    setup(state(), (c) =>
      c.url === '/api/v1/teams' && c.method === 'POST'
        ? JSON.parse(c.body!).name === 'Red'
          ? json(409, { type: 'urn:x4mp:problem:NameTaken', title: 'Conflict.', status: 409, code: 'NameTaken', detail: "The name 'Red' is taken" })
          : json(201, team(4, 'Gold'))
        : json(404, {}),
    );
    await screen.findByRole('article', { name: 'Team Red' });
    await user.click(screen.getByRole('button', { name: '+ New team' }));
    const dialog = screen.getByRole('dialog', { name: 'New team' });
    await user.click(within(dialog).getByRole('button', { name: 'Create team' }));
    expect(within(dialog).getByText('A name is required.')).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText('Name'), 'Red');
    await user.click(within(dialog).getByRole('button', { name: 'Create team' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent("The name 'Red' is taken");
    await user.clear(within(dialog).getByLabelText('Name'));
    await user.type(within(dialog).getByLabelText('Name'), 'Gold');
    await user.click(within(dialog).getByRole('button', { name: 'Create team' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(JSON.parse(sentTo('/api/v1/teams', 'POST')[1]!.body!)).toMatchObject({ name: 'Gold', locked: false, password: null });
  });

  it('saves the join mode as soon as it is chosen', async () => {
    const user = userEvent.setup();
    setup(state(), (c) =>
      c.url === '/api/v1/teams/policy' && c.method === 'PATCH' ? json(200, { ...state().policy, joinMode: 'AdminAssign' }) : json(404, {}),
    );
    await screen.findByRole('article', { name: 'Team Red' });
    expect(screen.getByRole('radio', { name: 'Auto' })).toBeChecked();
    await user.click(screen.getByRole('radio', { name: 'Admin assigns' }));
    await waitFor(() => expect(sentTo('/api/v1/teams/policy', 'PATCH')).toHaveLength(1));
    expect(JSON.parse(sentTo('/api/v1/teams/policy', 'PATCH')[0]!.body!)).toMatchObject({ joinMode: 'AdminAssign', autoAssign: null });
    await waitFor(() => expect(screen.getByRole('radio', { name: 'Admin assigns' })).toBeChecked());
  });
});
