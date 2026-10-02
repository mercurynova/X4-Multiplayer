export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let v = n / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v >= 100 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
}

export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(h)}:${pad(m)}:${pad(s % 60)}`;
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '-';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

export function percent(done: number, total: number): number {
  return total <= 0 ? 0 : Math.min(100, Math.floor((done / total) * 100));
}

/** The game's save time is epoch seconds as text; fall back to the upload time when it is missing or not a number. */
export function formatSaveTime(saveTime: string | null, uploadedAt: string): string {
  const n = saveTime && /^\d{9,11}$/.test(saveTime) ? Number(saveTime) : NaN;
  return Number.isNaN(n) ? formatDateTime(uploadedAt) : new Date(n * 1000).toLocaleString();
}
