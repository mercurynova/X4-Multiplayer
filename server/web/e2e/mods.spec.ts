import type { Page } from '@playwright/test';
import { request } from '@playwright/test';
import { e2e } from './env';
import { expect, test } from './fixtures';

const IN_GAME = /in game/;
const NEXUS = 'https://www.nexusmods.com/x4foundations/mods/1234';

// The shared fake authority reports the "modded" set (two DLC, two client-only libraries, a Workshop sim mod and a Nexus sim mod; see fixtures.ts).
test.describe.configure({ mode: 'serial' });

const panel = (page: Page, name: string) => page.getByRole('heading', { name, level: 2 }).locator('..');

test.describe('mods page', () => {
  test.beforeEach(({ authority }) => {
    expect(authority.running).toBe(true);
  });

  // Leave the shared server as the other specs expect it: the authority defines the list again and no entries are left.
  test.afterAll(async () => {
    const api = await request.newContext({ baseURL: e2e.httpUrl, storageState: e2e.storageState, extraHTTPHeaders: { 'X-X4MP': '1' } });
    const state = await (await api.get('/api/v1/mods')).json();
    for (const e of state.policy.entries) await api.delete(`/api/v1/mods/entries/${e.id}`);
    await api.patch('/api/v1/mods/policy', { data: { sourceMode: 'AuthorityDefines', unknownDefault: 'AllowClientOnly', enforcement: 'Strict' } });
    await api.dispose();
  });

  test('imports the authority, refuses a mismatching bot with the exact lists and links, and lets one in after an edit', async ({ page, bots }) => {
    await page.goto('/mods');
    await expect(page.getByRole('heading', { name: 'Mods', level: 1 })).toBeVisible();
    await expect(page.getByText(/Changes apply to the next join/)).toBeVisible();

    // Admin list + import from the authority (preview first, then confirm).
    await page.getByRole('combobox', { name: /Mod list/ }).selectOption('AdminList');
    await page.getByRole('button', { name: 'Import from authority' }).click();
    const importDialog = page.getByRole('dialog', { name: 'Import from authority' });
    const preview = importDialog.getByRole('table', { name: 'Import preview' });
    await expect(preview.getByRole('row', { name: /Warehouse Fleets.*Required/ })).toBeVisible();
    await expect(preview.getByRole('row', { name: /Mod Support APIs.*Allowed/ })).toBeVisible();
    await importDialog.getByRole('button', { name: /^Import \d+ mods$/ }).click();
    await expect(importDialog).toBeHidden();
    const list = page.getByRole('table', { name: 'Session mod list' });
    await expect(list.getByRole('row', { name: /Warehouse Fleets/ })).toBeVisible();
    await expect(list.getByRole('row', { name: /Better Traders/ })).toBeVisible();

    // Give Warehouse Fleets a Nexus link (a bad one is refused with a message under the field first).
    await page.getByRole('button', { name: 'Edit Warehouse Fleets', exact: true }).click();
    const edit = page.getByRole('dialog', { name: 'Edit Warehouse Fleets' });
    await edit.getByLabel(/Nexus URL/).fill('https://example.com/not-nexus');
    await edit.getByRole('button', { name: 'Save changes' }).click();
    await expect(edit.getByText(/Nexus Mods page/)).toBeVisible();
    await edit.getByLabel(/Nexus URL/).fill(NEXUS);
    await edit.getByRole('button', { name: 'Save changes' }).click();
    await expect(edit).toBeHidden();
    const nexus = list.getByRole('row', { name: /Warehouse Fleets/ }).getByRole('link', { name: 'Nexus' });
    await expect(nexus).toHaveAttribute('href', NEXUS);
    await expect(nexus).toHaveAttribute('target', '_blank');

    // A bot that lacks the required Workshop mod is refused; the page shows it live with the install list and the links.
    const refused = bots.start({ command: 'client', name: 'ModBotA', duration: 40, args: ['--extensions-preset', 'mismatch'] });
    expect(await refused.waitForExit(20_000)).not.toBeNull();
    
    expect(refused.output).not.toMatch(IN_GAME);
    const rejections = panel(page, 'Recent rejections');
    const row = rejections.getByRole('listitem').filter({ hasText: 'ModBotA' });
    await expect(row).toBeVisible();
    const install = row.getByRole('region', { name: 'Install list' });
    await expect(install).toContainText('Warehouse Fleets');
    await expect(install.getByRole('link', { name: 'Nexus' })).toHaveAttribute('href', NEXUS);
    await expect(install.getByRole('link', { name: 'Workshop' })).toHaveAttribute('href', /steamcommunity\.com.*2458720435/);
    await expect(install.getByRole('link', { name: 'Steam' })).toHaveAttribute('href', /^steam:\/\//);
    await expect(install.getByRole('link', { name: 'Nexus' })).toHaveAttribute('target', '_blank');

    // The same refusal through the API (what the page rendered).
    const rejected = (await (await page.request.get('/api/v1/mods/rejections')).json()) as { attemptedName: string; violation: { install: { id: string }[] } }[];
    expect(rejected.find((r) => r.attemptedName === 'ModBotA')?.violation.install.map((m) => m.id)).toEqual(['ws_2458720435']);

    // Make the mod Allowed: a new bot without it gets in, and its status shows up live.
    await page.getByRole('combobox', { name: 'Rule: Warehouse Fleets' }).selectOption('Allowed');
    await expect(page.getByRole('combobox', { name: 'Rule: Warehouse Fleets' })).toHaveValue('Allowed');
    const admitted = bots.start({ command: 'client', name: 'ModBotB', duration: 40, args: ['--extensions-preset', 'mismatch'] });
    await admitted.waitForLine(IN_GAME, 20_000);
    const players = panel(page, 'Players');
    const bob = players.getByRole('listitem').filter({ hasText: 'ModBotB' });
    await expect(bob).toBeVisible();
    await expect(bob.getByText('Admitted')).toBeVisible();

    // The Player detail page carries the same report.
    await bob.getByRole('link', { name: 'ModBotB' }).click();
    const section = page.getByLabel('Mods', { exact: true });
    await expect(section.getByText(/Latest report/)).toBeVisible();
    await expect(section.getByRole('table', { name: 'Report history' })).toBeVisible();
  });
});
