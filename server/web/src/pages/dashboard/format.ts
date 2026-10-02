export function formatUptime(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(Math.floor(s / 3600))}:${pad(Math.floor((s % 3600) / 60))}:${pad(s % 60)}`;
}

/** A rate given in KB/s as a short human string. */
export function formatRate(kBps: number): string {
  if (kBps >= 1024) return `${(kBps / 1024).toFixed(1)} MB/s`;
  if (kBps >= 10) return `${Math.round(kBps)} KB/s`;
  return `${kBps.toFixed(1)} KB/s`;
}

/** Milliseconds, with one decimal below 10 ms (LAN pings are fractional). */
export const formatMs = (ms: number) => `${ms < 10 ? ms.toFixed(1) : Math.round(ms)} ms`;

export const formatNumber = (n: number) => n.toLocaleString('en-US');
