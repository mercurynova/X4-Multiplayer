import { useEffect, useRef, type RefObject } from 'react';
import type { GalaxyFrameDto, GalaxyPlayerDto } from '../../generated/generated';
import { fitBounds, screenToWorld, worldToScreen, type Camera, type Viewport } from './camera';
import { fitCanvas, readCanvasTheme, type CanvasTheme } from './canvasTheme';
import {
  hexCorners,
  interpolateMarker,
  ownerColor,
  playerColor,
  sectorAt,
  type GalaxyLayout,
} from './galaxyLayout';
import type { GalaxyFrames } from './useGalaxy';
import { usePanZoom, type PanZoomOptions } from './usePanZoom';

export interface GalaxyLayers {
  players: boolean;
  interest: boolean;
  gates: boolean;
  heat: boolean;
}

interface Props {
  layout: GalaxyLayout;
  frames: RefObject<GalaxyFrames>;
  layers: GalaxyLayers;
  hoverSector: number | null;
  onHover: (sectorId: number | null) => void;
  /** Click on a sector (or on a player's marker, with that player to follow). */
  onOpen: (sectorId: number, followPlayerId: number | null) => void;
}

const MARKER_HIT_PX = 9;
const TIER_STYLE: Record<string, { width: number; dash: number[] }> = {
  Near: { width: 3, dash: [] },
  Sector: { width: 2.5, dash: [] },
  Adjacent: { width: 1.6, dash: [6, 3] },
  Linger: { width: 1.2, dash: [2, 3] },
};

/** The galaxy map: sector hexes at their map positions, gate/highway links, heat, interest outlines and live player markers. */
export function GalaxyCanvas({ layout, frames, layers, hoverSector, onHover, onOpen }: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const camRef = useRef<Camera>({ cx: 0, cy: 0, zoom: 1 });
  const vpRef = useRef<Viewport>({ w: 1, h: 1 });
  const optsRef = useRef<PanZoomOptions>({ minZoom: 0.01, maxZoom: 1000 });
  const stateRef = useRef({ layout, layers, hoverSector, onHover, onOpen });
  const fittedFor = useRef<GalaxyLayout | null>(null);

  useEffect(() => {
    stateRef.current = { layout, layers, hoverSector, onHover, onOpen };
  });

  const markerAt = (sx: number, sy: number): GalaxyPlayerDto | null => {
    const { layout: l } = stateRef.current;
    const f = frames.current;
    if (!f.curr) return null;
    const t = markerT(f);
    let best: GalaxyPlayerDto | null = null;
    let bestD = MARKER_HIT_PX * MARKER_HIT_PX;
    for (const p of f.curr.players) {
      const m = interpolateMarker(l, f.prev?.players.find((q) => q.id === p.id), p, t);
      if (!m) continue;
      const [x, y] = worldToScreen(camRef.current, vpRef.current, m[0], m[1]);
      const d = (x - sx) ** 2 + (y - sy) ** 2;
      if (d <= bestD) {
        best = p;
        bestD = d;
      }
    }
    return best;
  };

  useEffect(() => {
    optsRef.current = {
      minZoom: 0.01,
      maxZoom: 1000,
      onHover: (sx, sy) => {
        const [wx, wy] = screenToWorld(camRef.current, vpRef.current, sx, sy);
        const s = sectorAt(stateRef.current.layout, wx, wy);
        stateRef.current.onHover(s ? s.id : null);
        if (canvasRef.current) canvasRef.current.style.cursor = s ? 'pointer' : 'grab';
      },
      onLeave: () => stateRef.current.onHover(null),
      onClick: (sx, sy) => {
        const marker = markerAt(sx, sy);
        if (marker) {
          stateRef.current.onOpen(marker.sectorId, marker.id);
          return;
        }
        const [wx, wy] = screenToWorld(camRef.current, vpRef.current, sx, sy);
        const s = sectorAt(stateRef.current.layout, wx, wy);
        if (s) stateRef.current.onOpen(s.id, null);
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
    let aggFor: GalaxyFrameDto | null = null;
    let agg = new Map<number, number>();
    let maxShips = 1;
    const draw = (now: number) => {
      raf = requestAnimationFrame(draw);
      const { w, h, dpr } = fitCanvas(canvas);
      vpRef.current = { w, h };
      const { layout: l, layers: ly, hoverSector: hover } = stateRef.current;
      if (fittedFor.current !== l) {
        camRef.current = fitBounds(l.bounds, vpRef.current, 40, l.hexRadius * 4);
        fittedFor.current = l;
      }
      if (now - themeAt > 1000) {
        theme = readCanvasTheme(canvas);
        themeAt = now;
      }
      const ctx = canvas.getContext('2d');
      if (!ctx) return;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.fillStyle = theme.bg;
      ctx.fillRect(0, 0, w, h);
      const cam = camRef.current;
      const vp = vpRef.current;
      const fr = frames.current;
      if (fr.curr !== aggFor) {
        aggFor = fr.curr;
        agg = new Map((fr.curr?.sectorAgg ?? []).map((a) => [a.sectorId, a.ships] as const));
        maxShips = Math.max(1, ...agg.values());
      }
      const rPx = l.hexRadius * cam.zoom;
      const visible = (x: number, y: number) => x > -rPx * 2 && y > -rPx * 2 && x < w + rPx * 2 && y < h + rPx * 2;

      // links
      if (ly.gates) {
        ctx.lineWidth = 1;
        ctx.strokeStyle = theme.border;
        ctx.beginPath();
        for (const k of l.links) {
          if (!isGate(k.kind)) continue;
          const [x1, y1] = worldToScreen(cam, vp, k.a.x, k.a.y);
          const [x2, y2] = worldToScreen(cam, vp, k.b.x, k.b.y);
          ctx.moveTo(x1, y1);
          ctx.lineTo(x2, y2);
        }
        ctx.stroke();
        ctx.lineWidth = 2.5;
        ctx.strokeStyle = theme.accent;
        ctx.globalAlpha = 0.55;
        ctx.beginPath();
        for (const k of l.links) {
          if (isGate(k.kind)) continue;
          const [x1, y1] = worldToScreen(cam, vp, k.a.x, k.a.y);
          const [x2, y2] = worldToScreen(cam, vp, k.b.x, k.b.y);
          ctx.moveTo(x1, y1);
          ctx.lineTo(x2, y2);
        }
        ctx.stroke();
        ctx.globalAlpha = 1;
      }

      // sectors
      ctx.lineWidth = 1;
      for (const s of l.sectors) {
        const [x, y] = worldToScreen(cam, vp, s.x, s.y);
        if (!visible(x, y)) continue;
        hexPath(ctx, x, y, rPx);
        ctx.fillStyle = ownerColor(s.owner, 0.3);
        ctx.fill();
        if (ly.heat) {
          const ships = agg.get(s.id) ?? 0;
          if (ships > 0) {
            ctx.fillStyle = `rgba(255, 140, 40, ${0.08 + 0.5 * (ships / maxShips)})`;
            ctx.fill();
          }
        }
        ctx.strokeStyle = s.id === hover ? theme.text : ownerColor(s.owner, 0.9);
        ctx.lineWidth = s.id === hover ? 2.5 : 1;
        ctx.stroke();
        if (rPx > 20) {
          ctx.fillStyle = theme.text;
          ctx.font = `${Math.min(13, rPx * 0.28)}px system-ui, sans-serif`;
          ctx.textAlign = 'center';
          ctx.textBaseline = 'middle';
          ctx.fillText(s.name, x, y + rPx * 0.45);
        }
      }

      // interest outlines: one inset ring per player, line style by tier
      if (ly.interest && fr.curr && rPx > 4) {
        for (const e of fr.curr.interest) {
          const style = TIER_STYLE[e.tier] ?? TIER_STYLE['Linger']!;
          ctx.strokeStyle = playerColor(e.playerId, 0.95);
          ctx.lineWidth = style.width;
          ctx.setLineDash(style.dash);
          for (const sid of e.sectorIds) {
            const s = l.byId.get(sid);
            if (!s) continue;
            const [x, y] = worldToScreen(cam, vp, s.x, s.y);
            if (!visible(x, y)) continue;
            const n = ringCount.get(sid) ?? 0;
            ringCount.set(sid, n + 1);
            hexPath(ctx, x, y, Math.max(2, rPx - 2 - n * (style.width + 0.5)));
            ctx.stroke();
          }
        }
        ctx.setLineDash([]);
        ringCount.clear();
      }

      // players
      if (ly.players && fr.curr) {
        const t = markerT(fr);
        ctx.font = '12px system-ui, sans-serif';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'middle';
        for (const p of fr.curr.players) {
          const m = interpolateMarker(l, fr.prev?.players.find((q) => q.id === p.id), p, t);
          if (!m) continue;
          const [x, y] = worldToScreen(cam, vp, m[0], m[1]);
          if (x < -40 || y < -40 || x > w + 40 || y > h + 40) continue;
          const col = playerColor(p.id);
          const a = (p.headingDeg * Math.PI) / 180;
          ctx.fillStyle = col;
          ctx.strokeStyle = theme.bg;
          ctx.lineWidth = 2;
          ctx.beginPath();
          ctx.arc(x, y, 5, 0, Math.PI * 2);
          ctx.stroke();
          ctx.fill();
          ctx.strokeStyle = col;
          ctx.lineWidth = 2;
          ctx.beginPath();
          ctx.moveTo(x, y);
          ctx.lineTo(x + Math.sin(a) * 11, y - Math.cos(a) * 11);
          ctx.stroke();
          ctx.fillStyle = theme.text;
          ctx.fillText(p.name, x + 9, y - 8);
        }
      }
      canvas.dataset['sectors'] = String(l.sectors.length);
      canvas.dataset['players'] = String(fr.curr?.players.length ?? 0);
    };
    raf = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(raf);
  }, [frames]);

  return <canvas ref={canvasRef} className="map-canvas" data-testid="galaxy-canvas" aria-label="Galaxy map" role="img" />;
}

/** Link kinds arrive lower-case from the server ("gate", "highway"). */
const isGate = (kind: string) => kind.toLowerCase() === 'gate';

const ringCount = new Map<number, number>();

/** Progress between the previous and the newest galaxy frame (1 Hz): markers glide over one second. */
function markerT(f: GalaxyFrames): number {
  return (performance.now() - f.arrivedAt) / 1000;
}

function hexPath(ctx: CanvasRenderingContext2D, x: number, y: number, r: number): void {
  const c = hexCorners(x, y, r);
  ctx.beginPath();
  ctx.moveTo(c[0]!, c[1]!);
  for (let i = 2; i < 12; i += 2) ctx.lineTo(c[i]!, c[i + 1]!);
  ctx.closePath();
}
