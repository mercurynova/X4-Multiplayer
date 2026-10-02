/**
 * Hub names from server-design 4.6, kept in ONE place. M1-S3 owns the server hub and generated.ts; when the generated
 * contract exposes these constants, point this module at them and delete the duplicates. Payload types are `unknown`
 * here on purpose: pages cast to the generated DTO they expect (e.g. `DashboardSnapshotDto`).
 */

export const HUB_URL = '/hubs/admin';

/** Server -> client method names (IAdminClient). */
export const HUB_EVENTS = [
  'Dashboard',
  'PlayerChanged',
  'PlayerRemoved',
  'SessionChanged',
  'GalaxyFrame',
  'SectorFrame',
  'LogBatch',
  'Diagnostics',
  'Chat',
  'SaveTransfer',
  'Alert',
  'SettingsChanged',
  'TeamUpserted',
  'TeamDeleted',
  'TeamMemberChanged',
  'TeamRelationsChanged',
  'TeamPolicyChanged',
  'TeamsReset',
  'PlayerAwaitingTeam',
  'PermissionDenied',
  'WalletChanged',
  'LedgerPosted',
  'LoanChanged',
  'TradeChanged',
  'EconomyEvent',
  'EconomySummary',
  'EconomyAlert',
] as const;
export type HubEventName = (typeof HUB_EVENTS)[number];

/** Pseudo-events a group handler also receives. */
export const SNAPSHOT_EVENT = '$snapshot'; // the value returned by the Subscribe* call (dashboard snapshot, backfill, ...)
export const ERROR_EVENT = '$error'; // the Subscribe* call failed (payload: Error)

/** Shape of the `Alert` push. Not in the generated contract yet (AlertDto); confirm against M1-S3. */
export interface HubAlert {
  id?: string;
  severity: 'info' | 'warning' | 'error' | string;
  title?: string;
  message: string;
  /** Persistent alerts go in the banner slot until dismissed; others are toasts. */
  persistent?: boolean;
  code?: string;
}

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

const teamEvents = [
  'TeamUpserted',
  'TeamDeleted',
  'TeamMemberChanged',
  'TeamRelationsChanged',
  'TeamPolicyChanged',
  'TeamsReset',
  'PlayerAwaitingTeam',
  'PermissionDenied',
] as const;

/** Builders for every group. Use as `useHubGroup(groups.dashboard, handler)`. */
export const groups = {
  dashboard: {
    key: 'dashboard',
    subscribe: 'SubscribeDashboard',
    unsubscribe: 'UnsubscribeDashboard',
    args: [],
    events: ['Dashboard', 'PlayerChanged', 'PlayerRemoved', 'SessionChanged', 'SaveTransfer', 'TeamMemberChanged'],
  },
  galaxy: {
    key: 'galaxy',
    subscribe: 'SubscribeGalaxy',
    unsubscribe: null, // 4.6 lists no UnsubscribeGalaxy; the group is dropped on disconnect
    args: [],
    events: ['GalaxyFrame'],
  },
  sector: (sectorId: number): GroupSpec => ({
    key: `sector:${sectorId}`,
    subscribe: 'SubscribeSector',
    unsubscribe: 'UnsubscribeSector',
    args: [sectorId],
    events: ['SectorFrame'],
  }),
  logs: (filter: unknown): GroupSpec => ({
    key: `logs:${JSON.stringify(filter)}`,
    subscribe: 'SubscribeLogs',
    unsubscribe: 'UnsubscribeLogs',
    args: [filter],
    events: ['LogBatch'],
  }),
  diagnostics: {
    key: 'diag',
    subscribe: 'SubscribeDiagnostics',
    unsubscribe: 'UnsubscribeDiagnostics',
    args: [],
    events: ['Diagnostics'],
  },
  chat: { key: 'chat', subscribe: 'SubscribeChat', unsubscribe: null, args: [], events: ['Chat'] },
  teams: { key: 'teams', subscribe: 'SubscribeTeams', unsubscribe: 'UnsubscribeTeams', args: [], events: teamEvents },
  economy: {
    key: 'economy',
    subscribe: 'SubscribeEconomy',
    unsubscribe: 'UnsubscribeEconomy',
    args: [],
    events: ['WalletChanged', 'LedgerPosted', 'LoanChanged', 'TradeChanged', 'EconomyEvent', 'EconomySummary', 'EconomyAlert'],
  },
} as const satisfies Record<string, GroupSpec | ((...a: never[]) => GroupSpec)>;
