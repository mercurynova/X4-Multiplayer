import { useMemo, useRef, useState } from 'react';
import type { GalaxyDto, GalaxyFrameDto } from '../../generated/generated';
import { http } from '../../api/http';
import { E, groups, SNAPSHOT_EVENT } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { buildGalaxyLayout, type GalaxyLayout } from './galaxyLayout';

export interface GalaxyFrames {
  prev: GalaxyFrameDto | null;
  curr: GalaxyFrameDto | null;
  /** performance.now() when `curr` arrived. */
  arrivedAt: number;
}

export interface GalaxyState {
  layout: GalaxyLayout | null;
  /** The galaxy topic's frames in a ref (read by the canvases each animation frame) and the latest one as state (for panels). */
  frames: { current: GalaxyFrames };
  frame: GalaxyFrameDto | null;
}

/**
 * Joins the `galaxy` topic: the galaxy structure (the Subscribe call's answer, or REST once a frame shows it exists) and
 * the 1 Hz frames. The layout is null until the authority's GalaxyMetadata has arrived.
 */
export function useGalaxy(): GalaxyState {
  const [galaxy, setGalaxy] = useState<GalaxyDto | null>(null);
  const [frame, setFrame] = useState<GalaxyFrameDto | null>(null);
  const frames = useRef<GalaxyFrames>({ prev: null, curr: null, arrivedAt: 0 });
  const fetching = useRef(false);
  const have = useRef(false);

  const load = () => {
    if (fetching.current || have.current) return;
    fetching.current = true;
    http<GalaxyDto>('/api/v1/galaxy')
      .then((g) => {
        have.current = true;
        setGalaxy(g);
      })
      .catch(() => undefined)
      .finally(() => {
        fetching.current = false;
      });
  };

  useHubGroup(groups.galaxy, (event, payload) => {
    if (event === SNAPSHOT_EVENT) {
      const g = payload as GalaxyDto | null;
      if (g) {
        have.current = true;
        setGalaxy(g);
      }
    } else if (event === E.GalaxyFrame) {
      const f = payload as GalaxyFrameDto;
      const cur = frames.current;
      frames.current = { prev: cur.curr, curr: f, arrivedAt: performance.now() };
      setFrame(f);
      if (!have.current) load();
    }
  });

  const layout = useMemo(() => (galaxy ? buildGalaxyLayout(galaxy) : null), [galaxy]);
  return { layout, frames, frame };
}
