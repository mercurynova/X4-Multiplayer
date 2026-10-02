import type { APIRequestContext, Page } from '@playwright/test';
import { expect, test } from './fixtures';

const IN_GAME = /in game/;

interface Member {
  playerId: number;
  name: string;
  teamId: number | null;
  isAuthority: boolean;
}
interface State {
  teams: { id: number; name: string; memberCount: number }[];
  members: Member[];
  unassigned: Member[];
  relations: { entries: { teamA: number; teamB: number; relation: string }[]; defaultRelation: string };
}

const getState = async (api: APIRequestContext) => (await (await api.get('/api/v1/teams')).json()) as State;

const card = (page: Page, team: string) => page.getByRole('article', { name: `Team ${team}`, exact: true });
const memberRow = (page: Page, name: string) => page.locator(`li.member[data-player="${name}"]`);

/**
 * The specs share one server and one session, so earlier specs leave their bots behind (offline members, detached nodes).
 * A preset places everyone the session knows, so start from a roster with only the authority and Auto join mode.
 */
async function cleanSlate(api: APIRequestContext) {
  const dash = (await (await api.get('/api/v1/dashboard')).json()) as { players: { playerId: number; roles: string }[] };
  for (const p of dash.players) {
    if (!/authority/i.test(p.roles)) await api.post(`/api/v1/players/${p.playerId}/kick`, { data: { reason: 'e2e cleanup' } });
  }
  const state = await getState(api);
  for (const m of state.members) {
    if (!m.isAuthority) await api.put(`/api/v1/teams/members/${m.playerId}`, { data: { teamId: null, role: null } });
  }
  const res = await api.patch('/api/v1/teams/policy', { data: { joinMode: 'Auto', autoAssign: 'SingleTeam' } });
  expect(res.ok()).toBe(true);
}

const relation = (s: State, a: number, b: number) =>
  s.relations.entries.find((e) => e.teamA === Math.min(a, b) && e.teamB === Math.max(a, b))?.relation ?? s.relations.defaultRelation;

test.describe.configure({ mode: 'serial' });

test.describe('teams and factions', () => {
  test.beforeEach(({ authority }) => {
    expect(authority.running).toBe(true);
  });

  test('free-for-all with four bots gives four hostile teams, and a bot dragged onto another team follows within a second', async ({
    page,
    context,
    bots,
    api,
  }) => {
    await cleanSlate(api);
    for (const n of [1, 2, 3, 4]) {
      await bots.start({ command: 'client', name: `Tbot${n}`, duration: 150 }).waitForLine(IN_GAME);
    }

    await page.goto('/teams');
    await page.getByRole('button', { name: 'Free-for-all' }).click();
    const dialog = page.getByRole('alertdialog', { name: 'Apply preset: Free-for-all' });
    await expect(dialog.getByText(/every pair of teams becomes Hostile/)).toBeVisible();
    await dialog.getByRole('button', { name: /^Apply/ }).click();
    await expect(dialog).toBeHidden();

    // one team per bot (and one for the authority's player), each bot in its own
    for (const n of [1, 2, 3, 4]) await expect(card(page, `Tbot${n}`)).toBeVisible();
    for (const [a, b] of [[1, 2], [1, 3], [1, 4], [2, 3], [2, 4], [3, 4]] as const) {
      await expect(page.getByRole('button', { name: `Tbot${a} and Tbot${b}: Hostile`, exact: true })).toContainText('HOSTILE');
    }
    const state = await getState(api);
    const tbots = state.teams.filter((t) => t.name.startsWith('Tbot'));
    expect(tbots).toHaveLength(4);
    for (const t of tbots) for (const u of tbots) if (t.id < u.id) expect(relation(state, t.id, u.id)).toBe('Hostile');

    // A second browser is the "push" witness: it only learns about the move through the hub.
    const watcher = await context.newPage();
    await watcher.goto('/teams');
    await expect(memberRow(watcher, 'Tbot2')).toBeVisible();
    await expect(card(watcher, 'Tbot2')).toContainText('Tbot2');

    await memberRow(page, 'Tbot2').dragTo(card(page, 'Tbot1'));
    const droppedAt = Date.now();
    await expect(card(page, 'Tbot1').locator('li.member[data-player="Tbot2"]')).toBeVisible({ timeout: 1_000 });
    await expect(card(watcher, 'Tbot1').locator('li.member[data-player="Tbot2"]')).toBeVisible({ timeout: 1_000 });
    expect(Date.now() - droppedAt).toBeLessThan(2_000);
    await expect(card(watcher, 'Tbot2').locator('li.member[data-player="Tbot2"]')).toBeHidden();

    const after = await getState(api);
    const tbot1 = after.teams.find((t) => t.name === 'Tbot1')!;
    expect(after.members.find((m) => m.name === 'Tbot2')?.teamId).toBe(tbot1.id);
    const players = (await (await api.get('/api/v1/players')).json()) as { name: string; teamName: string | null }[];
    expect(players.find((p) => p.name === 'Tbot2')?.teamName).toBe('Tbot1');
    await watcher.close();
  });

  test('an Allied cell is applied, shown to every browser and kept by the server', async ({ page, context, api }) => {
    await page.goto('/teams');
    const watcher = await context.newPage();
    await watcher.goto('/teams');
    await expect(watcher.getByRole('button', { name: 'Tbot3 and Tbot4: Hostile', exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Tbot3 and Tbot4: Hostile', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Tbot4 and Tbot3: Allied (changed)', exact: true })).toContainText('ALLIED');
    await page.getByRole('button', { name: 'Apply relations (1)' }).click();

    await expect(page.getByRole('button', { name: 'Tbot3 and Tbot4: Allied', exact: true })).toBeVisible();
    await expect(watcher.getByRole('button', { name: 'Tbot3 and Tbot4: Allied', exact: true })).toBeVisible({ timeout: 2_000 });
    const state = await getState(api);
    const t3 = state.teams.find((t) => t.name === 'Tbot3')!;
    const t4 = state.teams.find((t) => t.name === 'Tbot4')!;
    expect(relation(state, t3.id, t4.id)).toBe('Allied');
    const t1 = state.teams.find((t) => t.name === 'Tbot1')!;
    expect(relation(state, t3.id, t1.id)).toBe('Hostile'); // the other pairs are untouched
    await watcher.close();
  });

  test('in admin-assign mode a new bot waits under Unassigned and downloads the save once it is placed', async ({ page, bots, api }) => {
    await page.goto('/teams');
    await page.getByRole('radio', { name: 'Admin assigns' }).click(); // saved at once; the radio follows the server
    await expect(page.getByRole('radio', { name: 'Admin assigns' })).toBeChecked();
    try {
      const bot = bots.start({ command: 'client', name: 'Tbot5', duration: 90 });
      await expect(page.getByRole('article', { name: 'Unassigned', exact: true }).locator('li.member[data-player="Tbot5"]')).toBeVisible();
      await expect(page.getByText('1 unassigned')).toBeVisible();
      expect(bot.output).not.toMatch(IN_GAME);

      await memberRow(page, 'Tbot5').dragTo(card(page, 'Tbot1'));
      await expect(card(page, 'Tbot1').locator('li.member[data-player="Tbot5"]')).toBeVisible();
      await bot.waitForLine(IN_GAME, 20_000); // it received the save, caught up and entered the game
      await expect(page.getByText(/\d+ unassigned/)).toBeHidden();
      expect((await getState(api)).unassigned.find((m) => m.name === 'Tbot5')).toBeUndefined();
    } finally {
      await api.patch('/api/v1/teams/policy', { data: { joinMode: 'Auto', autoAssign: 'SingleTeam' } });
    }
  });
});
