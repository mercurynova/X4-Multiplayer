/** Where a player is: "Argon Prime (12.3, -4.5 km)", or "" before the first position. Position is x and z in km inside the sector. */
export function formatWhere(live: { sectorId: number | null; sectorName: string | null; position: { x: number; z: number } | null }): string {
  if (live.sectorId === null) return '';
  const name = live.sectorName ?? `sector ${live.sectorId}`;
  return live.position ? `${name} (${(live.position.x / 1000).toFixed(1)}, ${(live.position.z / 1000).toFixed(1)} km)` : name;
}

/** "41h 12m", "3m 05s", "12s". */
export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (h > 0) return `${h}h ${String(m).padStart(2, '0')}m`;
  if (m > 0) return `${m}m ${String(s % 60).padStart(2, '0')}s`;
  return `${s}s`;
}

export function formatAgo(iso: string, now: number = Date.now()): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const s = Math.max(0, Math.floor((now - t) / 1000));
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86_400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86_400)} d ago`;
}

export function formatDateTime(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}
