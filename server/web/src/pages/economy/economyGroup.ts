import { AdminHubEvents as E, AdminHubMethods as M } from '../../generated/generated';
import type { GroupSpec } from '../../hub/contract';

/** The economy hub topic. SubscribeEconomy returns the EconomySummaryDto (delivered as `$snapshot`). */
export const economyGroup: GroupSpec = {
  key: 'economy',
  subscribe: M.SubscribeEconomy,
  unsubscribe: M.UnsubscribeEconomy,
  args: [],
  events: [E.WalletChanged, E.LedgerPosted, E.LoanChanged, E.TradeChanged, E.EconomyEvent, E.EconomySummary, E.EconomyAlert],
};
