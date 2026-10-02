/**
 * Hub names and DTOs come from the generated contract (generated.ts, owned by the server build). This module adds only the
 * client-side group registry (which Subscribe and Unsubscribe pair delivers which pushes). Topics are server-side
 * subscriptions, not SignalR groups; the HubManager re-issues them after a reconnect. Payloads are `unknown` here: pages cast
 * to the generated DTO they expect.
 */
import { AdminHubEvents, AdminHubMethods as M, type AlertDto, type LogFilterDto } from '../generated/generated';

export const HUB_URL = M.Route;
export type { AlertDto };

/** Server -> client method names. */
export const HUB_EVENTS: readonly string[] = Object.values(AdminHubEvents);
export type HubEventName = (typeof AdminHubEvents)[keyof typeof AdminHubEvents];
export const E = AdminHubEvents;

/** Pseudo-events a group handler also receives. */
export const SNAPSHOT_EVENT = '$snapshot'; // the value returned by the Subscribe* call (dashboard snapshot, backfill, ...)
export const ERROR_EVENT = '$error'; // the Subscribe* call failed (payload: Error)

/**
 * A subscription to one server-side group. `key` identifies the group for ref-counting; `subscribe`/`unsubscribe` are
 * hub methods (invoked with `args`); `events` are the pushes this group delivers.
 */
export interface GroupSpec {
  readonly key: string;
  readonly subscribe: string;
  readonly unsubscribe: string | null;
  readonly args: readonly unknown[];
  readonly events: readonly string[];
}

/** Builders for every group. Use as `useHubGroup(groups.dashboard, handler)`. */
export const groups = {
  dashboard: {
    key: 'dashboard',
    subscribe: M.SubscribeDashboard,
    unsubscribe: M.UnsubscribeDashboard,
    args: [],
    events: [E.Dashboard, E.PlayerChanged, E.PlayerRemoved, E.SessionChanged, E.SaveTransfer],
  },
  galaxy: {
    key: 'galaxy',
    subscribe: M.SubscribeGalaxy,
    unsubscribe: M.UnsubscribeGalaxy,
    args: [],
    events: [E.GalaxyFrame],
  },
  sector: (sectorId: number): GroupSpec => ({
    key: `sector:${sectorId}`,
    subscribe: M.SubscribeSector,
    unsubscribe: M.UnsubscribeSector,
    args: [sectorId],
    events: [E.SectorFrame],
  }),
  logs: (filter: LogFilterDto): GroupSpec => ({
    key: `logs:${JSON.stringify(filter)}`,
    subscribe: M.SubscribeLogs,
    unsubscribe: M.UnsubscribeLogs,
    args: [filter],
    events: [E.LogBatch],
  }),
  diagnostics: {
    key: 'diag',
    subscribe: M.SubscribeDiagnostics,
    unsubscribe: M.UnsubscribeDiagnostics,
    args: [],
    events: [E.Diagnostics],
  },
  chat: { key: 'chat', subscribe: M.SubscribeChat, unsubscribe: M.UnsubscribeChat, args: [], events: [E.Chat] },
} as const satisfies Record<string, GroupSpec | ((...a: never[]) => GroupSpec)>;
