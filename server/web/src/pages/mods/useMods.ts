import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/http';
import type { ModCatalogEntryDto, ModPolicyDto, ModsStateDto, PlayerModStatusDto, UnboundRejectionDto } from '../../generated/generated';
import { E, ERROR_EVENT, groups, SNAPSHOT_EVENT } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { modsApi } from './modsApi';

const messageOf = (e: unknown) => (e instanceof ApiError ? e.message : e instanceof Error ? e.message : 'Could not reach the server.');

/** A PlayerModsReported push into the state (replace by player id, add when new). */
export function applyPlayerReport(state: ModsStateDto, status: PlayerModStatusDto): ModsStateDto {
  const i = state.players.findIndex((p) => p.playerId === status.playerId);
  const players = i < 0 ? [...state.players, status] : state.players.map((p, j) => (j === i ? status : p));
  return { ...state, players };
}

/** A ModPolicyChanged push. The push carries the whole policy. */
export function applyPolicy(state: ModsStateDto, policy: ModPolicyDto): ModsStateDto {
  return { ...state, policy };
}

export interface Loaded<T> {
  data: T | null;
  /** True when the server answered 403 ModListHidden. */
  hidden: boolean;
  error: string | null;
}

function useLoaded<T>(load: () => Promise<T>, refreshKey: number): Loaded<T> {
  const [value, setValue] = useState<Loaded<T>>({ data: null, hidden: false, error: null });
  const loadRef = useRef(load);
  useEffect(() => {
    loadRef.current = load;
  });
  useEffect(() => {
    let alive = true;
    loadRef
      .current()
      .then((data) => alive && setValue({ data, hidden: false, error: null }))
      .catch((e: unknown) => {
        if (!alive) return;
        if (e instanceof ApiError && e.status === 403 && e.code === 'ModListHidden') setValue({ data: null, hidden: true, error: null });
        else setValue((cur) => ({ ...cur, error: messageOf(e) }));
      });
    return () => {
      alive = false;
    };
  }, [refreshKey]);
  return value;
}

/**
 * The live mods picture: one REST read to start from, then the hub's mods topic (`$snapshot` on every (re)subscribe, then
 * ModPolicyChanged and PlayerModsReported). The catalog and the rejections are REST reads that reload whenever a push arrives.
 */
export function useMods() {
  const [state, setState] = useState<ModsStateDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  const alive = useRef(true);

  const reload = useCallback(
    () =>
      modsApi
        .state()
        .then((s) => {
          if (!alive.current) return;
          setState(s);
          setError(null);
        })
        .catch((e: unknown) => alive.current && setError(messageOf(e))),
    [],
  );

  useEffect(() => {
    alive.current = true;
    void reload();
    return () => {
      alive.current = false;
    };
  }, [reload]);

  // A refusal bound to no player (an unknown key) arrives as UnboundRejectionReported; it reloads the by-key list.
  const [rejTick, setRejTick] = useState(0);
  useHubGroup(groups.mods, (event, payload) => {
    if (event === E.UnboundRejectionReported) {
      setRejTick((n) => n + 1);
      return;
    }
    if (event === ERROR_EVENT) {
      setError(messageOf(payload));
      return;
    }
    setError(null);
    if (event === SNAPSHOT_EVENT) setState(payload as ModsStateDto);
    else if (event === E.ModPolicyChanged) setState((cur) => (cur ? applyPolicy(cur, payload as ModPolicyDto) : cur));
    else if (event === E.PlayerModsReported) setState((cur) => (cur ? applyPlayerReport(cur, payload as PlayerModStatusDto) : cur));
    else return;
    setTick((t) => t + 1);
  });

  const catalog = useLoaded<ModCatalogEntryDto[]>(modsApi.catalog, tick);
  const rejections = useLoaded<UnboundRejectionDto[]>(modsApi.rejections, tick + rejTick);
  const refreshAll = useCallback(() => {
    void reload();
    setTick((t) => t + 1);
  }, [reload]);

  return { state, error, catalog, rejections, reload, refreshAll };
}
