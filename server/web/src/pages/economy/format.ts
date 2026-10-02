const nf = new Intl.NumberFormat('en-US');

/** `912,400` (whole credits). */
export const credits = (n: number): string => nf.format(n);

/** `+50,000` / `-50,000`. */
export const signedCredits = (n: number): string => (n > 0 ? '+' : '') + nf.format(n);

/** Local `HH:MM:SS`; ISO strings only. */
export function timeOf(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleTimeString([], { hour12: false });
}

export function dateTimeOf(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

export const walletKey = (kind: string, ownerId: number): string => `${kind.toLowerCase()}:${ownerId}`;

export const TX_KINDS = [
  'Donate', 'Transfer', 'PoolDeposit', 'PoolWithdraw', 'LoanEscrow', 'LoanDisburse', 'LoanRepay', 'LoanAutoRepay', 'LoanRefund',
  'TradeEscrow', 'TradeSettle', 'TradeRefund', 'GameIncome', 'GameSpend', 'AdminAdjust', 'Reversal', 'ModeMigration', 'TeamMove',
  'StartingCredits', 'SaveMoney',
] as const;

export const WALLET_KINDS = ['Player', 'TeamShared', 'TeamPool', 'Escrow', 'World'] as const;

export const OPEN_LOAN_STATES = ['Offered', 'Active', 'Overdue'];
export const OPEN_TRADE_STATES = ['Proposed', 'Countered', 'Accepted', 'Escrowed', 'Transferring', 'InDoubt'];
export const CANCELLABLE_TRADE_STATES = ['Proposed', 'Countered', 'Accepted', 'Escrowed'];
