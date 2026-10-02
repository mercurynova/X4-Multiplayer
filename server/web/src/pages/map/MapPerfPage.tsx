import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router';
import './map.css';
import { ALL_FILTERS, SectorCanvas, type SectorStats } from './SectorCanvas';
import { SectorInterpolator } from './sectorFrames';
import { SyntheticSector } from './syntheticFrames';

/**
 * Dev-only performance harness (`/map-perf?n=3000`): feeds the real sector canvas synthetic 4 Hz frames and shows the FPS.
 * Not routed in production builds. The Playwright perf spec reads `data-fps` from the canvas.
 */
export function MapPerfPage() {
  const [search] = useSearchParams();
  const n = Math.min(100_000, Math.max(10, Number(search.get('n') ?? 3000) || 3000));
  const [interp] = useState(() => new SectorInterpolator());
  const [stats, setStats] = useState<SectorStats | null>(null);

  useEffect(() => {
    interp.reset();
    const scene = new SyntheticSector(1, n);
    interp.push(scene.next(), performance.now());
    const timer = setInterval(() => interp.push(scene.next(), performance.now()), 250);
    return () => clearInterval(timer);
  }, [interp, n]);

  return (
    <section>
      <h1>Map performance harness</h1>
      <p data-testid="perf-stats">
        {n.toLocaleString()} synthetic entities at 4 Hz: {stats ? `${stats.fps.toFixed(1)} fps, ${stats.drawMs.toFixed(2)} ms per draw` : 'measuring'}
      </p>
      <div className="map-stage" style={{ height: 720 }}>
        <SectorCanvas
          interp={interp}
          playerNames={new Map([[1, 'Alpha'], [2, 'Bravo'], [3, 'Charlie']])}
          filters={ALL_FILTERS}
          trackPlayerId={null}
          trackNonce={0}
          selectedNetId={null}
          onHover={() => undefined}
          onSelect={() => undefined}
          onStats={setStats}
        />
      </div>
    </section>
  );
}
