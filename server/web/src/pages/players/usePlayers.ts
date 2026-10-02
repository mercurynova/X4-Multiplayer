import { useCallback, useEffect, useRef, useState } from 'react';
import type { DashboardSnapshotDto, PlayerDto, PlayerLiveDto } from '../../generated/generated';
import { E, groups, SNAPSHOT_EVENT } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { playersApi } from './playersApi';

export interface PlayerRow {
  player: PlayerDto;
  live: PlayerLiveDto | null;
  online: boolean;
}

/** Live roster: the persisted players (REST) joined with the live node state pushed on the dashboard group. */
export function usePlayers() {
  const [players, setPlayers] = useState<PlayerDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [live, setLive] = useState<ReadonlyMap<number, PlayerLiveDto>>(new Map());
  const [liveLoaded, setLiveLoaded] = useState(false);
  const known = useRef(new Set<number>());
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const refetch = useCallback(
    () =>
      playersApi
        .list()
        .then((list) => {
          known.current = new Set(list.map((p) => p.id));
          setPlayers(list);
          setError(null);
        })
        .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not load the players.')),
    [],
  );

  const scheduleRefetch = useCallback(() => {
    if (timer.current) return;
    timer.current = setTimeout(() => {
      timer.current = null;
      void refetch();
    }, 250);
  }, [refetch]);

  useEffect(() => {
    void refetch();
    return () => {
      if (timer.current) clearTimeout(timer.current);
    };
  }, [refetch]);

  useHubGroup(groups.dashboard, (event, payload) => {
    if (event === SNAPSHOT_EVENT || event === E.Dashboard) {
      const snap = payload as DashboardSnapshotDto;
      const next = new Map<number, PlayerLiveDto>();
      for (const p of snap.players ?? []) next.set(p.playerId, p);
      setLive(next);
      setLiveLoaded(true);
      if ([...next.keys()].some((id) => !known.current.has(id))) scheduleRefetch();
    } else if (event === E.PlayerChanged) {
      const p = payload as PlayerLiveDto;
      setLive((m) => new Map(m).set(p.playerId, p));
      if (!known.current.has(p.playerId)) scheduleRefetch();
    } else if (event === E.PlayerRemoved) {
      const id = Number(payload);
      setLive((m) => {
        const next = new Map(m);
        next.delete(id);
        return next;
      });
      scheduleRefetch(); // lastSeen / playtime moved
    }
  });

  const rows: PlayerRow[] | null =
    players?.map((player) => {
      const l = live.get(player.id) ?? null;
      return { player, live: l, online: l ? l.connected : liveLoaded ? false : player.online };
    }) ?? null;

  return { rows, error, refetch };
}
