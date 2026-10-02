import { useEffect, useRef } from 'react';
import { fitBounds, projectPoints, worldToScreen, type Camera, type Viewport } from './camera';
import { fitCanvas, readCanvasTheme, type CanvasTheme } from './canvasTheme';
import { playerColor } from './galaxyLayout';
import { nearestEntity } from './hitTest';
import { FLAG_DOCKED, Kind, KIND_COUNT, type SectorInterpolator } from './sectorFrames';
import { usePanZoom, type PanZoomOptions } from './usePanZoom';

export interface SectorFilters {
  /** Visible per ship class XS, S, M, L, XL. */
  ships: [boolean, boolean, boolean, boolean, boolean];
  stations: boolean;
  gates: boolean;
  other: boolean;
  docked: boolean;
}

export const ALL_FILTERS: SectorFilters = { ships: [true, true, true, true, true], stations: true, gates: true, other: true, docked: false };

export interface SectorStats {
  fps: number;
  /** Average milliseconds spent in the draw function over the last second. */
  drawMs: number;
  entities: number;
}

export interface Hovered {
  index: number;
  netId: number;
}

interface Props {
  interp: SectorInterpolator;
  /** Player id -> display name, for labels. */
  playerNames: ReadonlyMap<number, string>;
  filters: SectorFilters;
  /** Centre on this player's ship each frame until the user pans (re-armed by changing `trackNonce`). */
  trackPlayerId: number | null;
  trackNonce: number;
  selectedNetId: number | null;
  onHover: (h: Hovered | null) => void;
  onSelect: (netId: number | null) => void;
  onStats?: (s: SectorStats) => void;
  /** Called when the user pans or zooms (so the page can show that tracking stopped). */
  onUserMove?: () => void;
}

/** Ship half-size in px per class XS..XL; the on-screen size also grows a little with zoom. */
const SHIP_PX = [1.5, 2, 2.5, 3.5, 5] as const;
const HIT_PX = 8;
const MIN_ZOOM = 0.0004;
const MAX_ZOOM = 6;

function kindVisible(k: number, f: SectorFilters): boolean {
  if (k >= Kind.ShipXS && k <= Kind.ShipXL) return f.ships[k - 1]!;
  if (k === Kind.Station) return f.stations;
  if (k === Kind.Gate || k === Kind.Accelerator || k === Kind.HighwayEntry) return f.gates;
  return f.other;
}

/** A "nice" grid step (1, 2, 5 x 10^n metres) giving roughly 80-160 px between lines. */
export function niceGridStep(zoom: number): number {
  const target = 100 / zoom;
  const p = 10 ** Math.floor(Math.log10(target));
  const m = target / p;
  return (m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10) * p;
}

/** Top-down (X east, Z north) view of one sector from `SectorInterpolator`, drawn every animation frame with Canvas 2D. */
export function SectorCanvas(props: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const camRef = useRef<Camera>({ cx: 0, cy: 0, zoom: 0.01 });
  const vpRef = useRef<Viewport>({ w: 1, h: 1 });
  const optsRef = useRef<PanZoomOptions>({ minZoom: MIN_ZOOM, maxZoom: MAX_ZOOM });
  const propsRef = useRef(props);
  const screenRef = useRef({ x: new Float32Array(0), y: new Float32Array(0) });
  const trackRef = useRef(true);
  const fittedSector = useRef(0);

  useEffect(() => {
    propsRef.current = props;
  });
  const { trackPlayerId, trackNonce } = props;
  useEffect(() => {
    trackRef.current = true;
  }, [trackPlayerId, trackNonce]);

  const entityAt = (sx: number, sy: number): number => {
    const { interp, filters } = propsRef.current;
    const { x, y } = screenRef.current;
    if (x.length < interp.n) return -1;
    return nearestEntity(
      sx,
      sy,
      x,
      y,
      interp.order,
      interp.n,
      HIT_PX,
      (i) => {
        const k = interp.cls[i]!;
        const player = interp.playerIds[i] != null;
        if (!kindVisible(k, filters) || (!player && (interp.flags[i]! & FLAG_DOCKED) !== 0 && !filters.docked)) return -Infinity;
        return player ? 3 : k === Kind.Station ? 2 : k >= Kind.Gate && k <= Kind.HighwayEntry ? 2 : 1;
      },
    );
  };

  useEffect(() => {
    optsRef.current = {
      minZoom: MIN_ZOOM,
      maxZoom: MAX_ZOOM,
      onHover: (sx, sy) => {
        const i = entityAt(sx, sy);
        const p = propsRef.current;
        p.onHover(i < 0 ? null : { index: i, netId: p.interp.ids[i]! });
        if (canvasRef.current) canvasRef.current.style.cursor = i < 0 ? 'grab' : 'pointer';
      },
      onLeave: () => propsRef.current.onHover(null),
      onClick: (sx, sy) => {
        const i = entityAt(sx, sy);
        const p = propsRef.current;
        p.onSelect(i < 0 ? null : p.interp.ids[i]!);
      },
      onUserMove: () => {
        trackRef.current = false;
        propsRef.current.onUserMove?.();
      },
    };
  });
  usePanZoom(canvasRef, camRef, vpRef, optsRef);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    let raf = 0;
    let theme: CanvasTheme = readCanvasTheme(canvas);
    let themeAt = 0;
    let statsAt = performance.now();
    let frames = 0;
    let drawTotal = 0;
    const draw = (now: number) => {
      raf = requestAnimationFrame(draw);
      const t0 = performance.now();
      const { w, h, dpr } = fitCanvas(canvas);
      vpRef.current = { w, h };
      const p = propsRef.current;
      const { interp, filters } = p;
      const n = interp.n;
      if (now - themeAt > 1000) {
        theme = readCanvasTheme(canvas);
        themeAt = now;
      }
      const ctx = canvas.getContext('2d');
      if (!ctx) return;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.fillStyle = theme.bg;
      ctx.fillRect(0, 0, w, h);

      if (screenRef.current.x.length < interp.rx.length) {
        screenRef.current = { x: new Float32Array(interp.rx.length), y: new Float32Array(interp.rx.length) };
      }
      interp.sample(now);

      // First frame of a sector: fit the view around what is there (at least a 6 km box so a lone ship is not blown up).
      if (n > 0 && fittedSector.current !== interp.sectorId) {
        fittedSector.current = interp.sectorId;
        let minX = Infinity;
        let minZ = Infinity;
        let maxX = -Infinity;
        let maxZ = -Infinity;
        for (let i = 0; i < n; i++) {
          const x = interp.rx[i]!;
          const z = interp.rz[i]!;
          if (x < minX) minX = x;
          if (x > maxX) maxX = x;
          if (z < minZ) minZ = z;
          if (z > maxZ) maxZ = z;
        }
        camRef.current = fitBounds({ minX, minY: minZ, maxX, maxY: maxZ }, vpRef.current, 30, 6000);
        trackRef.current = true;
      }
      // Tracking a player: keep their ship at the centre.
      if (p.trackPlayerId !== null && trackRef.current) {
        const idx = interp.indexOfPlayer(p.trackPlayerId);
        if (idx >= 0) camRef.current = { ...camRef.current, cx: interp.rx[idx]!, cy: interp.rz[idx]! };
      }
      const cam = camRef.current;
      const vp = vpRef.current;
      const sx = screenRef.current.x;
      const sy = screenRef.current.y;
      projectPoints(cam, vp, interp.rx, interp.rz, n, sx, sy);

      drawGrid(ctx, cam, vp, theme);

      const order = interp.order;
      const dockedOk = filters.docked;
      for (let k = 1; k < KIND_COUNT; k++) {
        const a = interp.kindStart[k]!;
        const b = interp.kindStart[k + 1]!;
        if (a === b || !kindVisible(k, filters)) continue;
        const isShip = k <= Kind.ShipXL;
        ctx.fillStyle = kindColor(k, theme);
        if (isShip) {
          const half = SHIP_PX[k - 1]!;
          const s = half * 2;
          for (let j = a; j < b; j++) {
            const i = order[j]!;
            const x = sx[i]!;
            const y = sy[i]!;
            if (x < -10 || y < -10 || x > w + 10 || y > h + 10) continue;
            if ((interp.flags[i]! & FLAG_DOCKED) !== 0 && !dockedOk) continue;
            ctx.fillRect(x - half, y - half, s, s);
          }
        } else if (k === Kind.Station) {
          for (let j = a; j < b; j++) {
            const i = order[j]!;
            const x = sx[i]!;
            const y = sy[i]!;
            if (x < -10 || y < -10 || x > w + 10 || y > h + 10) continue;
            ctx.fillRect(x - 4, y - 4, 8, 8);
          }
        } else if (k === Kind.Gate || k === Kind.Accelerator || k === Kind.HighwayEntry) {
          ctx.strokeStyle = ctx.fillStyle;
          ctx.lineWidth = 2;
          for (let j = a; j < b; j++) {
            const i = order[j]!;
            const x = sx[i]!;
            const y = sy[i]!;
            if (x < -10 || y < -10 || x > w + 10 || y > h + 10) continue;
            ctx.beginPath();
            ctx.arc(x, y, 6, 0, Math.PI * 2);
            ctx.stroke();
          }
        } else {
          for (let j = a; j < b; j++) {
            const i = order[j]!;
            const x = sx[i]!;
            const y = sy[i]!;
            if (x < -10 || y < -10 || x > w + 10 || y > h + 10) continue;
            ctx.fillRect(x - 1.5, y - 1.5, 3, 3);
          }
        }
      }

      // Players on top: heading arrow plus name.
      ctx.font = '12px system-ui, sans-serif';
      ctx.textAlign = 'left';
      ctx.textBaseline = 'middle';
      for (let i = 0; i < n; i++) {
        const pid = interp.playerIds[i];
        if (pid == null) continue;
        const x = sx[i]!;
        const y = sy[i]!;
        const a = (interp.ryaw[i]! * Math.PI) / 180;
        const dx = Math.sin(a);
        const dy = -Math.cos(a);
        const col = playerColor(pid);
        ctx.fillStyle = col;
        ctx.strokeStyle = theme.bg;
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.moveTo(x + dx * 10, y + dy * 10);
        ctx.lineTo(x - dx * 6 - dy * 6, y - dy * 6 + dx * 6);
        ctx.lineTo(x - dx * 6 + dy * 6, y - dy * 6 - dx * 6);
        ctx.closePath();
        ctx.stroke();
        ctx.fill();
        ctx.fillStyle = theme.text;
        ctx.fillText(p.playerNames.get(pid) ?? `Player ${pid}`, x + 12, y - 10);
      }

      // Selection ring.
      if (p.selectedNetId !== null) {
        const i = interp.indexOfId(p.selectedNetId);
        if (i >= 0) {
          ctx.strokeStyle = theme.text;
          ctx.lineWidth = 1.5;
          ctx.beginPath();
          ctx.arc(sx[i]!, sy[i]!, 11, 0, Math.PI * 2);
          ctx.stroke();
        }
      }

      drawScale(ctx, cam, vp, theme);

      frames++;
      drawTotal += performance.now() - t0;
      if (now - statsAt >= 1000) {
        const fps = (frames * 1000) / (now - statsAt);
        const stats = { fps, drawMs: drawTotal / frames, entities: n };
        canvas.dataset['fps'] = fps.toFixed(1);
        canvas.dataset['drawMs'] = stats.drawMs.toFixed(2);
        p.onStats?.(stats);
        frames = 0;
        drawTotal = 0;
        statsAt = now;
      }
      canvas.dataset['entities'] = String(n);
      canvas.dataset['sector'] = String(interp.sectorId);
    };
    raf = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(raf);
  }, []);

  return <canvas ref={canvasRef} className="map-canvas" data-testid="sector-canvas" aria-label="Sector map" role="img" />;
}

function kindColor(k: number, t: CanvasTheme): string {
  switch (k) {
    case Kind.ShipXS:
      return '#7e8ea3';
    case Kind.ShipS:
      return '#6fa8dc';
    case Kind.ShipM:
      return '#4fc3c7';
    case Kind.ShipL:
      return '#8bd36b';
    case Kind.ShipXL:
      return '#e6c34a';
    case Kind.Station:
      return '#e08a3c';
    case Kind.Gate:
      return '#b48cf2';
    case Kind.Accelerator:
    case Kind.HighwayEntry:
      return t.accent;
    default:
      return t.muted;
  }
}

function drawGrid(ctx: CanvasRenderingContext2D, cam: Camera, vp: Viewport, theme: CanvasTheme): void {
  const step = niceGridStep(cam.zoom);
  ctx.strokeStyle = theme.border;
  ctx.globalAlpha = 0.55;
  ctx.lineWidth = 1;
  ctx.beginPath();
  const [x0] = worldToScreen(cam, vp, Math.floor((cam.cx - vp.w / 2 / cam.zoom) / step) * step, 0);
  for (let x = x0, i = 0; x < vp.w + 1 && i < 400; x += step * cam.zoom, i++) {
    const px = Math.round(x) + 0.5;
    ctx.moveTo(px, 0);
    ctx.lineTo(px, vp.h);
  }
  const [, y0] = worldToScreen(cam, vp, 0, Math.floor((cam.cy - vp.h / 2 / cam.zoom) / step) * step);
  for (let y = y0, i = 0; y > -1 && i < 400; y -= step * cam.zoom, i++) {
    const py = Math.round(y) + 0.5;
    ctx.moveTo(0, py);
    ctx.lineTo(vp.w, py);
  }
  ctx.stroke();
  ctx.globalAlpha = 1;
  // origin cross
  const [ox, oy] = worldToScreen(cam, vp, 0, 0);
  ctx.strokeStyle = theme.muted;
  ctx.beginPath();
  ctx.moveTo(ox - 8, oy);
  ctx.lineTo(ox + 8, oy);
  ctx.moveTo(ox, oy - 8);
  ctx.lineTo(ox, oy + 8);
  ctx.stroke();
}

function drawScale(ctx: CanvasRenderingContext2D, cam: Camera, vp: Viewport, theme: CanvasTheme): void {
  const step = niceGridStep(cam.zoom);
  const len = step * cam.zoom;
  const x = 14;
  const y = vp.h - 16;
  ctx.strokeStyle = theme.text;
  ctx.fillStyle = theme.text;
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.moveTo(x, y);
  ctx.lineTo(x + len, y);
  ctx.stroke();
  ctx.font = '11px system-ui, sans-serif';
  ctx.textAlign = 'left';
  ctx.textBaseline = 'bottom';
  ctx.fillText(step >= 1000 ? `${step / 1000} km` : `${step} m`, x, y - 4);
}
