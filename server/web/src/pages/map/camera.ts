/**
 * 2D pan/zoom camera shared by the galaxy and sector canvases. World coordinates have +y up (north), screen +y down,
 * so the projection flips y. `zoom` is pixels per world unit (km in the galaxy, metres in a sector).
 */
export interface Camera {
  cx: number;
  cy: number;
  zoom: number;
}

export interface Viewport {
  w: number;
  h: number;
}

export interface Bounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export function worldToScreen(cam: Camera, vp: Viewport, x: number, y: number): [number, number] {
  return [(x - cam.cx) * cam.zoom + vp.w / 2, vp.h / 2 - (y - cam.cy) * cam.zoom];
}

export function screenToWorld(cam: Camera, vp: Viewport, sx: number, sy: number): [number, number] {
  return [(sx - vp.w / 2) / cam.zoom + cam.cx, (vp.h / 2 - sy) / cam.zoom + cam.cy];
}

/** Projects `n` world points (parallel arrays) to screen space, writing into `outX`/`outY` (no allocation). */
export function projectPoints(
  cam: Camera,
  vp: Viewport,
  xs: ArrayLike<number>,
  ys: ArrayLike<number>,
  n: number,
  outX: Float32Array,
  outY: Float32Array,
): void {
  const z = cam.zoom;
  const ox = vp.w / 2 - cam.cx * z;
  const oy = vp.h / 2 + cam.cy * z;
  for (let i = 0; i < n; i++) {
    outX[i] = xs[i]! * z + ox;
    outY[i] = oy - ys[i]! * z;
  }
}

export function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}

/** Zooms by `factor` keeping the world point under the screen position (sx, sy) fixed. */
export function zoomAt(cam: Camera, vp: Viewport, sx: number, sy: number, factor: number, minZoom: number, maxZoom: number): Camera {
  const zoom = clamp(cam.zoom * factor, minZoom, maxZoom);
  const [wx, wy] = screenToWorld(cam, vp, sx, sy);
  return { zoom, cx: wx - (sx - vp.w / 2) / zoom, cy: wy - (vp.h / 2 - sy) / zoom };
}

/** Moves the camera so the world follows a drag of (dx, dy) screen pixels. */
export function panBy(cam: Camera, dx: number, dy: number): Camera {
  return { ...cam, cx: cam.cx - dx / cam.zoom, cy: cam.cy + dy / cam.zoom };
}

/** The camera that shows `b` fully inside the viewport with `padPx` margin; a degenerate box gets `minSpan` world units. */
export function fitBounds(b: Bounds, vp: Viewport, padPx = 24, minSpan = 1): Camera {
  const spanX = Math.max(b.maxX - b.minX, minSpan);
  const spanY = Math.max(b.maxY - b.minY, minSpan);
  const zoom = Math.min(Math.max(vp.w - 2 * padPx, 10) / spanX, Math.max(vp.h - 2 * padPx, 10) / spanY);
  return { cx: (b.minX + b.maxX) / 2, cy: (b.minY + b.maxY) / 2, zoom };
}
