import { useCallback, useEffect, useState } from 'react';
import { api } from '../../api/http';
import type { AlertDto, DashboardSnapshotDto, MetricSeriesDto, PlayerLiveDto } from '../../generated/generated';
import { E, groups, SNAPSHOT_EVENT } from '../../hub/contract';
import { useHubGroup, useHubState } from '../../hub/HubProvider';

export const HISTORY = 600;
const BYTES_IN = ['net.bytes_in.control', 'net.bytes_in.realtime', 'net.bytes_in.bulk'];
const BYTES_OUT = ['net.bytes_out.control', 'net.bytes_out.realtime', 'net.bytes_out.bulk'];
const METRIC_NAMES = [...BYTES_IN, ...BYTES_OUT];

export interface DashboardHistory {
  kBpsIn: number[];
  kBpsOut: number[];
  /** Per-player RTT ms, keyed by player id; built from pushes since the page opened. */
  ping: Record<number, { name: string; values: number[] }>;
}

export interface DashboardState {
  snapshot: DashboardSnapshotDto | null;
  players: PlayerLiveDto[];
  alerts: AlertDto[];
  history: DashboardHistory;
  /** True once the first snapshot arrived. */
  loaded: boolean;
}

const cap = (a: number[]) => (a.length > HISTORY ? a.slice(a.length - HISTORY) : a);

/** Sums the named series sample by sample (right-aligned: newest sample last) and converts B/s to KB/s. */
const sumSeries = (series: MetricSeriesDto[], names: string[]): number[] => {
  if (!Array.isArray(series)) return [];
  const picked = series.filter((s) => names.includes(s.name));
  const len = Math.max(0, ...picked.map((s) => s.samples.length));
  const out: number[] = [];
  for (let i = 0; i < len; i++) {
    let total = 0;
    for (const s of picked) total += s.samples[s.samples.length - len + i] ?? 0;
    out.push(total / 1024);
  }
  return out;
};

/**
 * Dashboard data: the `dashboard` topic (snapshot on subscribe, `Dashboard` every second, immediate player pushes) plus the
 * 10-minute traffic backfill from `/diagnostics/metrics`, fetched on mount and after every reconnect.
 */
export function useDashboard(): DashboardState {
  const [snapshot, setSnapshot] = useState<DashboardSnapshotDto | null>(null);
  const [players, setPlayers] = useState<PlayerLiveDto[]>([]);
  const [alerts, setAlerts] = useState<AlertDto[]>([]);
  const [history, setHistory] = useState<DashboardHistory>({ kBpsIn: [], kBpsOut: [], ping: {} });
  const hubState = useHubState();

  const backfill = useCallback(() => {
    api
      .get<MetricSeriesDto[]>(`/api/v1/diagnostics/metrics?series=${METRIC_NAMES.join(',')}&window=${HISTORY}`)
      .then((series) => setHistory((h) => ({ ...h, kBpsIn: sumSeries(series, BYTES_IN), kBpsOut: sumSeries(series, BYTES_OUT) })))
      .catch(() => undefined);
  }, []);

  useEffect(() => {
    if (hubState === 'connected') backfill();
  }, [hubState, backfill]);

  const apply = (s: DashboardSnapshotDto, appendHistory: boolean) => {
    setSnapshot(s);
    setPlayers(s.players);
    setAlerts(s.activeAlerts);
    if (!appendHistory) return;
    setHistory((h) => {
      const ping = { ...h.ping };
      for (const p of s.players) {
        if (!p.connected) continue;
        ping[p.playerId] = { name: p.name, values: cap([...(ping[p.playerId]?.values ?? []), p.rttMs]) };
      }
      return { kBpsIn: cap([...h.kBpsIn, s.traffic.kBpsIn]), kBpsOut: cap([...h.kBpsOut, s.traffic.kBpsOut]), ping };
    });
  };

  useHubGroup(groups.dashboard, (event, payload) => {
    switch (event) {
      case SNAPSHOT_EVENT:
        apply(payload as DashboardSnapshotDto, false);
        break;
      case E.Dashboard:
        apply(payload as DashboardSnapshotDto, true);
        break;
      case E.PlayerChanged: {
        const p = payload as PlayerLiveDto;
        setPlayers((cur) =>
          cur.some((x) => x.playerId === p.playerId) ? cur.map((x) => (x.playerId === p.playerId ? p : x)) : [...cur, p],
        );
        break;
      }
      case E.PlayerRemoved:
        setPlayers((cur) => cur.filter((x) => x.playerId !== (payload as number)));
        break;
      case E.SessionChanged:
        setSnapshot((cur) => (cur ? { ...cur, session: payload as DashboardSnapshotDto['session'] } : cur));
        break;
      default:
        break;
    }
  });

  return { snapshot, players, alerts, history, loaded: snapshot !== null };
}
