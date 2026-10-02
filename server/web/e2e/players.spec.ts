import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';

const IN_GAME = /in game/;

/** The Players table row of one player. */
const row = (page: Page, name: string) => page.getByRole('row').filter({ has: page.getByRole('link', { name, exact: true }) });

async function reasonDialog(page: Page, title: string, reason: string, confirm: string) {
  const dialog = page.getByRole('dialog', { name: title });
  await dialog.getByRole('textbox', { name: 'Reason' }).fill(reason);
  await dialog.getByRole('button', { name: confirm, exact: true }).click();
  await expect(dialog).toBeHidden();
}

test.describe.configure({ mode: 'serial' });

test.describe('players', () => {
  // A session needs the authority's checkpoint before clients can join (worker-scoped fixture).
  test.beforeEach(({ authority }) => {
    expect(authority.running).toBe(true);
  });

  test('lists a joining bot live, with the Viewer-visible facts', async ({ page, bots }) => {
    await page.goto('/players');
    const bot = bots.start({ command: 'client', name: 'Lister', duration: 60 });
    await bot.waitForLine(IN_GAME);
    const r = row(page, 'Lister');
    await expect(r.getByText(/Online/)).toBeVisible();
    await expect(r.getByText('127.0.0.1')).toBeVisible();
    await page.getByRole('searchbox', { name: 'Search players' }).fill('nobody-by-this-name');
    await expect(page.getByText('No players match.')).toBeVisible();
    await page.getByRole('searchbox', { name: 'Search players' }).fill('liste');
    await expect(r).toBeVisible();
    await r.getByRole('link', { name: 'Lister' }).click();
    await expect(page.getByRole('heading', { name: 'Lister', level: 1 })).toBeVisible();
    await expect(page.getByText('Connection', { exact: true })).toBeVisible();
  });

  test('kick disconnects the bot and the list shows it offline within 2 s; ban blocks it; unban lets it back', async ({ page, bots }) => {
    await page.goto('/players');
    let bot = bots.start({ command: 'client', name: 'Victim', duration: 90 });
    await bot.waitForLine(IN_GAME);
    await expect(row(page, 'Victim').getByText(/Online/)).toBeVisible();

    // Kick: the bot's connection ends and the row flips to Offline.
    await row(page, 'Victim').getByRole('button', { name: 'Kick Victim' }).click();
    const kickedAt = Date.now();
    await reasonDialog(page, 'Kick Victim', 'e2e kick', 'Kick');
    await expect(row(page, 'Victim').getByText('Offline')).toBeVisible({ timeout: 2_000 });
    expect(await bot.waitForExit(2_000)).not.toBeNull();
    expect(Date.now() - kickedAt).toBeLessThan(3_500);

    // Ban (permanent): the bot cannot get back in.
    bot = bots.start({ command: 'client', name: 'Victim', duration: 90 });
    await bot.waitForLine(IN_GAME);
    await expect(row(page, 'Victim').getByText(/Online/)).toBeVisible();
    await row(page, 'Victim').getByRole('button', { name: 'Ban Victim' }).click();
    await reasonDialog(page, 'Ban Victim', 'e2e ban', 'Ban');
    await expect(row(page, 'Victim').getByText('Offline')).toBeVisible({ timeout: 2_000 });
    await expect(row(page, 'Victim').getByText('BAN', { exact: true })).toBeVisible();
    expect(await bot.waitForExit(3_000)).not.toBeNull();

    const banned = bots.start({ command: 'client', name: 'Victim', duration: 30 });
    expect(await banned.waitForExit(15_000)).not.toBeNull();
    expect(banned.output).not.toMatch(IN_GAME);
    await expect(row(page, 'Victim').getByText('Offline')).toBeVisible();

    // Unban: it can join again.
    await row(page, 'Victim').getByRole('button', { name: 'Unban Victim' }).click();
    await page.getByRole('dialog', { name: 'Unban Victim' }).getByRole('button', { name: 'Unban', exact: true }).click();
    await expect(row(page, 'Victim').getByText('BAN', { exact: true })).toBeHidden();
    const back = bots.start({ command: 'client', name: 'Victim', duration: 60 });
    await back.waitForLine(IN_GAME);
    await expect(row(page, 'Victim').getByText(/Online/)).toBeVisible();
  });

  test('detail page: notes persist; kick without a reason is refused', async ({ page, bots }) => {
    const bot = bots.start({ command: 'client', name: 'Noted', duration: 60 });
    await bot.waitForLine(IN_GAME);
    await page.goto('/players');
    await row(page, 'Noted').getByRole('link', { name: 'Noted' }).click();
    await page.getByRole('textbox', { name: 'Admin notes' }).fill('likes trains');
    await page.getByRole('button', { name: 'Save notes' }).click();
    await expect(page.getByText('Notes saved.')).toBeVisible();
    await page.reload();
    await expect(page.getByRole('textbox', { name: 'Admin notes' })).toHaveValue('likes trains');

    await page.getByRole('button', { name: 'Kick Noted' }).click();
    const dialog = page.getByRole('dialog', { name: 'Kick Noted' });
    await dialog.getByRole('button', { name: 'Kick', exact: true }).click();
    await expect(dialog.getByText('A reason is required.')).toBeVisible();
    expect(bot.running).toBe(true);
  });
});
