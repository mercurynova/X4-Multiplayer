export interface SparklineSeries {
  label: string;
  values: readonly number[];
}

const COLORS = ['var(--accent)', 'var(--ok)', 'var(--warn)', 'var(--danger)', 'var(--muted)'];

/**
 * Minimal SVG line chart for one or several series. Renders `data-points` (the longest series' sample count) so tests and
 * screen readers can see how much history is shown; the accessible name carries the latest values.
 */
export function Sparkline({
  series,
  label,
  width = 220,
  height = 40,
}: {
  series: readonly SparklineSeries[];
  label: string;
  width?: number;
  height?: number;
}) {
  const max = Math.max(1, ...series.flatMap((s) => s.values));
  const longest = Math.max(0, ...series.map((s) => s.values.length));
  const summary = series
    .map((s) => `${s.label} ${s.values.length ? Math.round(s.values[s.values.length - 1]!) : 'no data'}`)
    .join(', ');
  return (
    <svg
      role="img"
      aria-label={`${label}: ${summary}`}
      data-points={longest}
      viewBox={`0 0 ${width} ${height}`}
      width="100%"
      height={height}
      preserveAspectRatio="none"
      className="sparkline"
    >
      {series.map((s, i) => {
        if (s.values.length < 2) return null;
        const step = width / Math.max(1, longest - 1);
        const offset = (longest - s.values.length) * step;
        const pts = s.values
          .map((v, j) => `${(offset + j * step).toFixed(1)},${(height - 2 - (v / max) * (height - 4)).toFixed(1)}`)
          .join(' ');
        return <polyline key={s.label} points={pts} fill="none" stroke={COLORS[i % COLORS.length]} strokeWidth="1.5" vectorEffect="non-scaling-stroke" />;
      })}
    </svg>
  );
}
