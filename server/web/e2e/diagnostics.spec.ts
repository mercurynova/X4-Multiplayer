import { expect, test } from './fixtures';

const IN_GAME = /in game/;
const SELFTEST_SENT = /self-test sent/;

test.describe.configure({ mode: 'serial' });

test.describe('node diagnostics (M2-13)', () => {
  // A session needs the authority's checkpoint before clients can join (worker-scoped fixture).
  test.beforeEach(({ authority }) => {
    expect(authority.running).toBe(true);
  });

  test('a FakeNode --selftest table and its log lines appear on the Players detail page within 2 s', async ({ page, bots, api }) => {
    const bot = bots.start({ command: 'client', name: 'SelfTester', duration: 90, args: ['--selftest'] });
    await bot.waitForLine(IN_GAME);

    // Find the player id from the roster, open the detail page, then wait for the node's own "sent" line is already past or about to come.
    await expect
      .poll(async () => ((await (await api.get('/api/v1/players?limit=1000')).json()) as { name: string }[]).some((p) => p.name === 'SelfTester'), {
        timeout: 15_000,
      })
      .toBe(true);
    const players = (await (await api.get('/api/v1/players?limit=1000')).json()) as { id: number; name: string }[];
    const id = players.find((p) => p.name === 'SelfTester')!.id;
    await bot.waitForLine(SELFTEST_SENT);
    const sentAt = Date.now();

    await page.goto(`/players/${id}`);
    const panel = page.getByLabel('Diagnostics');
    const table = panel.getByRole('table', { name: 'Self-test' });
    await expect(table.getByRole('row', { name: /x4native\.api PASS game 9\.00 x4native 9\.0\.0 exports 15\/15/ })).toBeVisible({ timeout: 2_000 });
    await expect(table.getByRole('row', { name: /game\.adapter FAIL/ })).toBeVisible();
    await expect(table.getByRole('row', { name: /main_thread SKIP/ })).toBeVisible();
    await expect(panel.getByText('3 passed, 1 failed, 1 warnings, 1 skipped')).toBeVisible();
    await expect(panel.getByLabel('Forwarded log lines')).toContainText('fakenode: self-test starting');
    expect(Date.now() - sentAt).toBeLessThan(5_000); // navigation included; the 2 s bound above is the table itself

    // The same through REST.
    const dto = (await (await api.get(`/api/v1/players/${id}/diagnostics`)).json()) as { selfTest: { overall: string; rows: unknown[] } };
    expect(dto.selfTest.overall).toBe('FAIL');
    expect(dto.selfTest.rows).toHaveLength(6);
  });

  test('a table sent while the page is open shows up live', async ({ page, bots, api }) => {
    // The bot joins first without a self-test; the page for its (future) player id is opened as soon as the roster knows it.
    const bot = bots.start({ command: 'client', name: 'LiveTester', duration: 90 });
    await bot.waitForLine(IN_GAME);
    let id = 0;
    await expect
      .poll(async () => {
        const list = (await (await api.get('/api/v1/players?limit=1000')).json()) as { id: number; name: string }[];
        id = list.find((p) => p.name === 'LiveTester')?.id ?? 0;
        return id;
      })
      .toBeGreaterThan(0);
    await page.goto(`/players/${id}`);
    await expect(page.getByText('No self-test table received from this player yet.')).toBeVisible();

    // A second bot under the same name cannot be started, so the table comes from a self-test bot that takes over the identity.
    bot.stop();
    const again = bots.start({ command: 'client', name: 'LiveTester', duration: 90, args: ['--selftest'] });
    await again.waitForLine(SELFTEST_SENT);
    await expect(page.getByLabel('Diagnostics').getByRole('table', { name: 'Self-test' })).toBeVisible({ timeout: 2_000 });
  });
});
