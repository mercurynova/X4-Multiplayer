import type { Page } from '@playwright/test';
import type { DashboardSnapshotDto, GalaxyDto } from '../src/generated/generated';
import { expect, test } from './fixtures';

const IN_GAME = /in game/;

/** The bits of a canvas the pixel probe uses (the e2e project has no DOM lib). */
interface CanvasLike {
  width: number;
  height: number;
  getContext(kind: '2d'): { getImageData(x: number, y: number, w: number, h: number): { data: Uint8ClampedArray } } | null;
}

/** The "(x, z km)" position text of a player's row in the galaxy panel. */
const positionOf = (page: Page, name: string) => page.getByTestId(`galaxy-player-${name}`).locator('.map-pos');

test.describe.configure({ mode: 'serial' });

test.describe('map', () => {
  test.beforeEach(({ authority }) => {
    expect(authority.running).toBe(true);
  });

  test('galaxy view shows the sectors and player markers that move with a swarm', async ({ page, bots, api }) => {
    const galaxy = (await (await api.get('/api/v1/galaxy')).json()) as GalaxyDto;
    expect(galaxy.sectors.length).toBeGreaterThan(50);

    await page.goto('/map');
    const canvas = page.getByTestId('galaxy-canvas');
    await expect(canvas).toBeVisible();
    await expect(canvas).toHaveAttribute('data-sectors', String(galaxy.sectors.length));

    const swarm = bots.start({ command: 'swarm', clients: 4, namePrefix: 'Marker', duration: 90 });
    await swarm.waitForLine(IN_GAME);
    await expect(page.getByRole('heading', { name: /Players \([4-9]\)/ })).toBeVisible({ timeout: 20_000 });
    await expect(canvas).toHaveAttribute('data-players', /^[4-9]$/);

    // The markers move: the position of a player changes between two galaxy frames.
    const first = await positionOf(page, 'Marker01').innerText();
    await expect.poll(() => positionOf(page, 'Marker01').innerText(), { timeout: 15_000 }).not.toBe(first);

    // The canvas actually drew something (not a blank rectangle): sample pixels for non-background colours.
    const drawn = await canvas.evaluate((el) => {
      const c = el as unknown as CanvasLike;
      const ctx = c.getContext('2d');
      if (!ctx) return 0;
      const data = ctx.getImageData(0, 0, c.width, c.height).data;
      const seen = new Set<number>();
      for (let i = 0; i < data.length; i += 4 * 97) seen.add((data[i]! << 16) | (data[i + 1]! << 8) | data[i + 2]!);
      return seen.size;
    });
    expect(drawn).toBeGreaterThan(5);

    // Hovering shows the details panel with the interest overlay and a click opens the sector view.
    await page.getByRole('button', { name: 'Follow Marker01' }).click();
    await expect(page).toHaveURL(/\/map\/\d+\?follow=\d+$/);
    await expect(page.getByTestId('following')).toContainText('Marker01');
  });

  test('opening a sector with nobody in it makes the authority capture it and shows entities; the interest list shows clients', async ({ page, bots, api }) => {
    const galaxy = (await (await api.get('/api/v1/galaxy')).json()) as GalaxyDto;
    const bot = bots.start({ command: 'client', name: 'Watcher', duration: 90, args: ['--behavior', 'wander'] });
    await bot.waitForLine(IN_GAME);

    // Find where the bot is, then open a different sector that has ships (an empty one would also be fine).
    await page.goto('/map');
    const where = positionOf(page, 'Watcher');
    await expect(where).toBeVisible({ timeout: 20_000 });
    const text = await where.innerText();
    const botSector = galaxy.sectors.find((s) => text.startsWith(s.name));
    expect(botSector).toBeTruthy();
    const other = galaxy.sectors.find((s) => s.id !== botSector!.id)!;

    const before = (await (await api.get('/api/v1/dashboard')).json()) as DashboardSnapshotDto;
    await page.goto(`/map/${other.id}`);
    await expect(page.getByTestId('sector-name')).toHaveText(other.name);
    const count = page.getByTestId('entity-count');
    await expect.poll(async () => Number(await count.getAttribute('data-count')), { timeout: 20_000 }).toBeGreaterThan(0);
    await expect(page.getByTestId('sector-canvas')).toHaveAttribute('data-entities', /^[1-9]\d*$/);

    // The admin view joined the capture set.
    await expect
      .poll(async () => ((await (await api.get('/api/v1/dashboard')).json()) as DashboardSnapshotDto).sectorsCaptured, { timeout: 10_000 })
      .toBeGreaterThanOrEqual(Math.max(1, before.sectorsCaptured));

    // The bot's own sector view lists the bot and its interest tier.
    await page.goto(`/map/${botSector!.id}`);
    await expect(page.getByTestId('sector-players')).toContainText('Watcher', { timeout: 15_000 });
    await expect(page.getByTestId('interest-list')).toContainText('Watcher');
    await expect(page.getByTestId('interest-list')).toContainText(/Near|Sector/);
  });

  test('following a bot through a gate jump switches the sector view', async ({ page, bots, api }) => {
    test.setTimeout(150_000);
    const galaxy = (await (await api.get('/api/v1/galaxy')).json()) as GalaxyDto;
    // FakeNode's explorers are deterministic (seed 42, bot index 2): Jump02 starts in sector 134 and takes its first
    // gate to sector 133 about 63 s after it is in game, and stays there for ~40 s (the session holds 8 nodes, so index 8,
    // which jumps after 21 s, does not fit next to the authority). A bot that bounced straight back
    // would be unobservable at the galaxy topic's 1 Hz.
    const swarm = bots.start({ command: 'swarm', clients: 2, namePrefix: 'Jump', duration: 140, args: ['--behavior', 'explore'] });
    const line = await swarm.waitForLine(/\[Jump02\] in game, flying Explore from sector (\d+)/, 60_000);
    const startSector = Number(/sector (\d+)/.exec(line)![1]);

    await page.goto('/map');
    await page.getByRole('button', { name: 'Follow Jump02' }).click({ timeout: 20_000 });
    await expect(page).toHaveURL(/\/map\/\d+\?follow=\d+$/);
    const startName = await page.getByTestId('sector-name').innerText();
    const sectorOf = (url: string) => Number(/\/map\/(\d+)/.exec(url)![1]);
    // It may already have jumped by now; the point is that the view ends up where the bot is, still following it.
    await expect.poll(() => sectorOf(page.url()), { timeout: 100_000, intervals: [500] }).not.toBe(startSector);
    const endSector = sectorOf(page.url());
    expect(galaxy.sectors.some((s) => s.id === endSector)).toBe(true);
    await expect(page.getByTestId('sector-name')).toHaveText(galaxy.sectors.find((s) => s.id === endSector)!.name);
    await expect(page.getByTestId('sector-name')).not.toHaveText(startName);
    await expect(page.getByTestId('following')).toContainText('Jump02');
    await expect(page.getByTestId('sector-players')).toContainText('Jump02', { timeout: 15_000 });
    await expect(page.getByTestId('entity-count')).toHaveAttribute('data-count', /^[1-9]\d*$/, { timeout: 20_000 });
  });
});
