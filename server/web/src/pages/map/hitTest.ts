/**
 * The entity nearest to the screen point within `radiusPx`, or -1. `order` lists the candidate indices (e.g. only the
 * visible kinds); a later index in `order` wins ties so what is drawn on top is picked first. `priority` (optional, per
 * entity) wins over distance when set to a larger value, e.g. players over the ships around them.
 */
export function nearestEntity(
  sx: number,
  sy: number,
  screenX: ArrayLike<number>,
  screenY: ArrayLike<number>,
  order: ArrayLike<number>,
  count: number,
  radiusPx: number,
  priority?: (index: number) => number,
): number {
  let best = -1;
  let bestScore = Infinity;
  let bestPriority = -Infinity;
  const r2 = radiusPx * radiusPx;
  for (let k = 0; k < count; k++) {
    const i = order[k]!;
    const dx = screenX[i]! - sx;
    const dy = screenY[i]! - sy;
    const d2 = dx * dx + dy * dy;
    if (d2 > r2) continue;
    const pr = priority ? priority(i) : 0;
    if (pr === -Infinity) continue; // not selectable (filtered out)
    if (pr > bestPriority || (pr === bestPriority && d2 <= bestScore)) {
      best = i;
      bestScore = d2;
      bestPriority = pr;
    }
  }
  return best;
}
