import { useEffect, type RefObject } from 'react';
import { panBy, zoomAt, type Camera, type Viewport } from './camera';

export interface PanZoomOptions {
  minZoom: number;
  maxZoom: number;
  /** A press-release without a drag, at canvas-local CSS pixels. */
  onClick?: (sx: number, sy: number) => void;
  onDoubleClick?: (sx: number, sy: number) => void;
  /** Pointer position over the canvas, or null when it left. */
  onHover?: (sx: number, sy: number) => void;
  onLeave?: () => void;
  /** The user moved the camera (drag or wheel): a following view should stop recentring if it wants to. */
  onUserMove?: () => void;
}

const DRAG_THRESHOLD_PX = 4;

/** Wheel = zoom around the cursor, drag = pan, click and hover callbacks. The camera lives in a ref the draw loop reads. */
export function usePanZoom(
  canvasRef: RefObject<HTMLCanvasElement | null>,
  camRef: RefObject<Camera>,
  vpRef: RefObject<Viewport>,
  optionsRef: RefObject<PanZoomOptions>,
): void {
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    let drag: { x: number; y: number; moved: boolean } | null = null;
    const local = (e: MouseEvent | PointerEvent | WheelEvent): [number, number] => {
      const r = canvas.getBoundingClientRect();
      return [e.clientX - r.left, e.clientY - r.top];
    };
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const o = optionsRef.current;
      const [sx, sy] = local(e);
      const factor = Math.exp(-e.deltaY * 0.0015);
      camRef.current = zoomAt(camRef.current, vpRef.current, sx, sy, factor, o.minZoom, o.maxZoom);
      o.onUserMove?.();
    };
    const onDown = (e: PointerEvent) => {
      if (e.button !== 0) return;
      const [sx, sy] = local(e);
      drag = { x: sx, y: sy, moved: false };
      canvas.setPointerCapture(e.pointerId);
    };
    const onMove = (e: PointerEvent) => {
      const [sx, sy] = local(e);
      const o = optionsRef.current;
      if (drag) {
        const dx = sx - drag.x;
        const dy = sy - drag.y;
        if (!drag.moved && Math.hypot(dx, dy) < DRAG_THRESHOLD_PX) return;
        drag.moved = true;
        camRef.current = panBy(camRef.current, dx, dy);
        drag.x = sx;
        drag.y = sy;
        o.onUserMove?.();
      } else {
        o.onHover?.(sx, sy);
      }
    };
    const onUp = (e: PointerEvent) => {
      const d = drag;
      drag = null;
      if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
      if (d && !d.moved) {
        const [sx, sy] = local(e);
        optionsRef.current.onClick?.(sx, sy);
      }
    };
    const onLeave = () => optionsRef.current.onLeave?.();
    const onDbl = (e: MouseEvent) => {
      const [sx, sy] = local(e);
      optionsRef.current.onDoubleClick?.(sx, sy);
    };
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('pointerdown', onDown);
    canvas.addEventListener('pointermove', onMove);
    canvas.addEventListener('pointerup', onUp);
    canvas.addEventListener('pointerleave', onLeave);
    canvas.addEventListener('dblclick', onDbl);
    return () => {
      canvas.removeEventListener('wheel', onWheel);
      canvas.removeEventListener('pointerdown', onDown);
      canvas.removeEventListener('pointermove', onMove);
      canvas.removeEventListener('pointerup', onUp);
      canvas.removeEventListener('pointerleave', onLeave);
      canvas.removeEventListener('dblclick', onDbl);
    };
  }, [canvasRef, camRef, vpRef, optionsRef]);
}
