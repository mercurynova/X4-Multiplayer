import type { MetricSeriesDto } from '../../generated/generated';

const W = 320;
const H = 90;

function fmt(n: number): string {
  const a = Math.abs(n);
  if (a >= 1e9) return `${(n / 1e9).toFixed(1)}G`;
  if (a >= 1e6) return `${(n / 1e6).toFixed(1)}M`;
  if (a >= 1e3) return `${(n / 1e3).toFixed(1)}k`;
  return Number.isInteger(n) ? String(n) : n.toFixed(1);
}

/** Dependency-free line chart: one polyline per series on a shared scale, with a legend of latest values. */
export function MetricChart({ title, unit, series }: { title: string; unit: string; series: MetricSeriesDto[] }) {
  const max = Math.max(1, ...series.flatMap((s) => s.samples));
  const longest = Math.max(2, ...series.map((s) => s.samples.length));
  const points = (s: MetricSeriesDto) =>
    s.samples.map((v, i) => `${((i + longest - s.samples.length) / (longest - 1)) * W},${H - (v / max) * (H - 4) - 2}`).join(' ');

  return (
    <figure className="metric-chart">
      <figcaption>
        {title}
        {unit ? ` (${unit})` : ''}
      </figcaption>
      {series.length === 0 ? (
        <p className="muted">No data yet.</p>
      ) : (
        <>
          <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={`${title}, peak ${fmt(max)} ${unit}`}>
            <line x1="0" y1={H - 1} x2={W} y2={H - 1} className="axis" />
            {series.map((s, i) => (
              <polyline key={s.name} points={points(s)} className={`line line-${i % 3}`} fill="none" />
            ))}
          </svg>
          <ul className="legend">
            {series.map((s, i) => (
              <li key={s.name}>
                <span className={`swatch line-${i % 3}`} aria-hidden="true" /> {s.name.replace(/^(net|process)\./, '')}:{' '}
                {fmt(s.samples[s.samples.length - 1] ?? 0)} <span className="muted">(peak {fmt(Math.max(0, ...s.samples))})</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </figure>
  );
}
