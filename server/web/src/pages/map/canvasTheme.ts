/** Canvas colours read from the CSS theme tokens (a canvas cannot use `var(--x)`), refreshed now and then so a theme switch shows. */
export interface CanvasTheme {
  bg: string;
  surface: string;
  text: string;
  muted: string;
  border: string;
  accent: string;
}

const FALLBACK: CanvasTheme = { bg: '#12151a', surface: '#1b2027', text: '#e6e9ee', muted: '#9aa4b2', border: '#2d343e', accent: '#4c8dff' };

export function readCanvasTheme(el: Element): CanvasTheme {
  try {
    const cs = getComputedStyle(el);
    const v = (name: string, fb: string) => cs.getPropertyValue(name).trim() || fb;
    return {
      bg: v('--bg', FALLBACK.bg),
      surface: v('--surface', FALLBACK.surface),
      text: v('--text', FALLBACK.text),
      muted: v('--muted', FALLBACK.muted),
      border: v('--border', FALLBACK.border),
      accent: v('--accent', FALLBACK.accent),
    };
  } catch {
    return FALLBACK;
  }
}

/** Sizes a canvas to its CSS box times the device pixel ratio and returns the CSS-pixel viewport. */
export function fitCanvas(canvas: HTMLCanvasElement): { w: number; h: number; dpr: number } {
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const w = Math.max(1, Math.round(canvas.clientWidth));
  const h = Math.max(1, Math.round(canvas.clientHeight));
  const pw = Math.round(w * dpr);
  const ph = Math.round(h * dpr);
  if (canvas.width !== pw || canvas.height !== ph) {
    canvas.width = pw;
    canvas.height = ph;
  }
  return { w, h, dpr };
}
