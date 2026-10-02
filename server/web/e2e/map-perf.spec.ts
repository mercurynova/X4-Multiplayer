import { expect, test } from './fixtures';

/**
 * Rendering performance of the sector canvas with synthetic frames (the dev-only /map-perf harness, 4 Hz updates).
 * The numbers are printed and attached to the report; the assertion is a deliberately low floor so a loaded CI machine
 * does not flake (the target is 50 fps at 3,000 entities; see docs/roadmap.md M1-W4).
 */
for (const n of [3000, 10000]) {
  test(`sector canvas renders ${n} entities`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1400, height: 1000 });
    await page.goto(`/map-perf?n=${n}`);
    const canvas = page.getByTestId('sector-canvas');
    await expect(canvas).toHaveAttribute('data-entities', String(n));
    // Skip the warm-up second, then average three one-second windows.
    await page.waitForTimeout(2_000);
    const samples: number[] = [];
    const draws: number[] = [];
    for (let i = 0; i < 3; i++) {
      await page.waitForTimeout(1_050);
      samples.push(Number(await canvas.getAttribute('data-fps')));
      draws.push(Number(await canvas.getAttribute('data-draw-ms')));
    }
    const fps = samples.reduce((a, b) => a + b, 0) / samples.length;
    const drawMs = draws.reduce((a, b) => a + b, 0) / draws.length;
    const line = `PERF ${n} entities: ${fps.toFixed(1)} fps (samples ${samples.join(', ')}), ${drawMs.toFixed(2)} ms per draw, canvas ${await canvas.evaluate((c) => `${(c as unknown as { width: number }).width}x${(c as unknown as { height: number }).height}`)}`;
    console.log(line);
    testInfo.annotations.push({ type: 'perf', description: line });
    expect(fps).toBeGreaterThan(20);
  });
}
