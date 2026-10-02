import { describe, expect, it } from 'vitest';
import { fitBounds, panBy, projectPoints, screenToWorld, worldToScreen, zoomAt, type Camera, type Viewport } from './camera';

const vp: Viewport = { w: 800, h: 600 };
const cam: Camera = { cx: 100, cy: 50, zoom: 2 };

describe('camera projection', () => {
  it('puts the camera centre at the viewport centre and flips y (north up)', () => {
    expect(worldToScreen(cam, vp, 100, 50)).toEqual([400, 300]);
    const [x, y] = worldToScreen(cam, vp, 110, 60);
    expect(x).toBe(420);
    expect(y).toBe(280); // north is up the screen
  });

  it('round-trips world -> screen -> world', () => {
    const [sx, sy] = worldToScreen(cam, vp, -33.5, 71.25);
    const [wx, wy] = screenToWorld(cam, vp, sx, sy);
    expect(wx).toBeCloseTo(-33.5, 6);
    expect(wy).toBeCloseTo(71.25, 6);
  });

  it('projectPoints matches worldToScreen for every entity', () => {
    const xs = [0, 100, -250.5, 1e4];
    const zs = [0, 50, 12, -9000];
    const ox = new Float32Array(4);
    const oy = new Float32Array(4);
    projectPoints(cam, vp, xs, zs, 4, ox, oy);
    for (let i = 0; i < 4; i++) {
      const [sx, sy] = worldToScreen(cam, vp, xs[i]!, zs[i]!);
      expect(ox[i]).toBeCloseTo(sx, 1);
      expect(oy[i]).toBeCloseTo(sy, 1);
    }
  });

  it('zoomAt keeps the point under the cursor fixed and respects the zoom limits', () => {
    const before = screenToWorld(cam, vp, 600, 200);
    const z = zoomAt(cam, vp, 600, 200, 3, 0.1, 100);
    expect(z.zoom).toBe(6);
    const after = screenToWorld(z, vp, 600, 200);
    expect(after[0]).toBeCloseTo(before[0], 6);
    expect(after[1]).toBeCloseTo(before[1], 6);
    expect(zoomAt(cam, vp, 0, 0, 1e6, 0.1, 100).zoom).toBe(100);
    expect(zoomAt(cam, vp, 0, 0, 1e-6, 0.1, 100).zoom).toBe(0.1);
  });

  it('panBy moves the world with the drag', () => {
    const p = panBy(cam, 20, -10);
    // dragging right by 20 px moves the world right: the centre moves left in world space
    expect(p.cx).toBe(100 - 10);
    expect(p.cy).toBe(50 - 5);
  });

  it('fitBounds shows the whole box, centred, and tolerates a single point', () => {
    const c = fitBounds({ minX: 0, minY: 0, maxX: 1000, maxY: 500 }, { w: 1000, h: 500 }, 0);
    expect(c).toEqual({ cx: 500, cy: 250, zoom: 1 });
    const one = fitBounds({ minX: 5, minY: 5, maxX: 5, maxY: 5 }, vp, 0, 100);
    expect(Number.isFinite(one.zoom)).toBe(true);
    expect(one.zoom).toBe(6); // 600 px over the 100-unit minimum span
  });
});
