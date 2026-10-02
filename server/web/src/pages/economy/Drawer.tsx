import { useEffect, useState, type ReactNode } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import type {
  EconomyEventDto,
  LedgerTxDto,
  LoanDto,
  TradeOfferDto,
  WalletDetailDto,
  WalletDto,
} from '../../generated/generated';
import { AdjustDialog, FreezeDialog, ReverseDialog } from './Dialogs';
import { describeEconomyError, economyApi } from './economyApi';
import { useEconomy, type DrawerTarget } from './EconomyContext';
import { credits, dateTimeOf, signedCredits, timeOf } from './format';
import { LoanStateChip } from './LoansTab';
import { itemText, TradeStateChip } from './TradesTab';
import { parties } from './TransactionsTab';
import { WalletFlags } from './WalletsTab';

type Load<T> = { data: T | null; error: string | null };

/** Fetches once per `key`; `bump` (a version number from the live data) refreshes it. */
function useLoaded<T>(fetcher: () => Promise<T>, key: string, bump: number | string = 0): Load<T> {
  const [state, setState] = useState<Load<T> & { key: string }>({ data: null, error: null, key });
  useEffect(() => {
    let cancelled = false;
    fetcher()
      .then((data) => !cancelled && setState({ data, error: null, key }))
      .catch((e: unknown) => !cancelled && setState({ data: null, error: describeEconomyError(e), key }));
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, bump]);
  return state.key === key ? state : { data: null, error: null };
}

function Events({ events }: { events: EconomyEventDto[] }) {
  if (events.length === 0) return <p className="muted">No events recorded.</p>;
  return (
    <ol className="events">
      {events.map((e) => (
        <li key={e.id}>
          <span className="mono">{timeOf(e.at)}</span> {e.type}
          {e.fromState && e.toState ? ` (${e.fromState} → ${e.toState})` : ''} <span className="muted">{e.actor}</span>
          {e.reason ? ` — ${e.reason}` : ''}
        </li>
      ))}
    </ol>
  );
}

function TxList({ txs }: { txs: LedgerTxDto[] }) {
  const { openDrawer } = useEconomy();
  if (txs.length === 0) return <p className="muted">No transactions.</p>;
  return (
    <ul className="plain">
      {txs.map((tx) => {
        const p = parties(tx);
        return (
          <li key={tx.id}>
            <button type="button" className="link" onClick={() => openDrawer({ type: 'tx', id: tx.id })}>
              <span className="mono">{timeOf(tx.at)}</span> {tx.kind}
            </button>{' '}
            {p.from} → {p.to}, {credits(p.amount)}
            {tx.reversedBy && <span className="flag flag-warn">reversed</span>}
          </li>
        );
      })}
    </ul>
  );
}

function WalletDrawer({ kind, ownerId }: { kind: string; ownerId: number }) {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { wallets, openDrawer } = useEconomy();
  const live = wallets?.find((w) => w.kind === kind && w.ownerId === ownerId) ?? null;
  const { data, error } = useLoaded<WalletDetailDto>(() => economyApi.wallet(kind, ownerId), `${kind}:${ownerId}`, live?.version ?? 0);
  const [adjust, setAdjust] = useState(false);
  const [freeze, setFreeze] = useState(false);
  const wallet: WalletDto | null = live ?? data?.wallet ?? null;
  return (
    <>
      {error && <p role="alert">{error}</p>}
      {wallet && (
        <>
          <p className="drawer-balance">
            {credits(wallet.balance)} Cr <WalletFlags wallet={wallet} />
          </p>
          {wallet.frozen && wallet.frozenReason && <p className="muted">Frozen: {wallet.frozenReason}</p>}
          {isAdmin && wallet.kind !== 'World' && (
            <div className="actions">
              <button type="button" className="ghost" onClick={() => setAdjust(true)}>
                Adjust…
              </button>
              <button type="button" className="ghost" onClick={() => setFreeze(true)}>
                {wallet.frozen ? 'Unfreeze…' : 'Freeze…'}
              </button>
            </div>
          )}
        </>
      )}
      {data && (
        <>
          <h3>Ledger (latest {data.recent.length})</h3>
          <TxList txs={data.recent} />
          <h3>Open loans</h3>
          {data.openLoans.length === 0 ? (
            <p className="muted">None.</p>
          ) : (
            <ul className="plain">
              {data.openLoans.map((l) => (
                <li key={l.id}>
                  <button type="button" className="link" onClick={() => openDrawer({ type: 'loan', id: l.id })}>
                    Loan #{l.id}
                  </button>{' '}
                  {l.lender} → {l.borrower}, {credits(l.outstanding)} outstanding <LoanStateChip loan={l} />
                </li>
              ))}
            </ul>
          )}
          <h3>Open trades</h3>
          {data.openTrades.length === 0 ? (
            <p className="muted">None.</p>
          ) : (
            <ul className="plain">
              {data.openTrades.map((t) => (
                <li key={t.id}>
                  <button type="button" className="link" onClick={() => openDrawer({ type: 'trade', id: t.id })}>
                    Trade #{t.id}
                  </button>{' '}
                  {t.initiator} ↔ {t.counterparty} <TradeStateChip trade={t} />
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      {adjust && wallet && <AdjustDialog wallet={wallet} onClose={() => setAdjust(false)} onDone={(m) => toast('success', m)} />}
      {freeze && wallet && <FreezeDialog wallet={wallet} onClose={() => setFreeze(false)} onDone={(m) => toast('success', m)} />}
    </>
  );
}

function TxDrawer({ id }: { id: string }) {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { txs, openDrawer } = useEconomy();
  const listed = txs?.find((t) => t.id === id);
  const { data, error } = useLoaded<LedgerTxDto>(() => economyApi.transaction(id), id, listed?.reversedBy ?? '');
  const [reversing, setReversing] = useState(false);
  const tx = data ?? listed ?? null;
  return (
    <>
      {error && <p role="alert">{error}</p>}
      {tx && (
        <>
          <dl className="facts">
            <dt>Time</dt>
            <dd>{dateTimeOf(tx.at)}</dd>
            <dt>Kind</dt>
            <dd>{tx.kind}</dd>
            <dt>Actor</dt>
            <dd>{tx.actor}</dd>
            {tx.requestId && (
              <>
                <dt>Request id</dt>
                <dd className="mono">{tx.requestId}</dd>
              </>
            )}
            {tx.refType && (
              <>
                <dt>Related</dt>
                <dd>
                  {tx.refType} #{tx.refId}
                </dd>
              </>
            )}
            {tx.note && (
              <>
                <dt>Note</dt>
                <dd>{tx.note}</dd>
              </>
            )}
            {tx.reverses && (
              <>
                <dt>Reverses</dt>
                <dd>
                  <button type="button" className="link mono" onClick={() => openDrawer({ type: 'tx', id: tx.reverses ?? '' })}>
                    {tx.reverses}
                  </button>
                </dd>
              </>
            )}
            {tx.reversedBy && (
              <>
                <dt>Reversed by</dt>
                <dd>
                  <button type="button" className="link mono" onClick={() => openDrawer({ type: 'tx', id: tx.reversedBy ?? '' })}>
                    {tx.reversedBy}
                  </button>
                </dd>
              </>
            )}
          </dl>
          <h3>Legs</h3>
          <table className="data">
            <caption className="visually-hidden">Double-entry legs</caption>
            <thead>
              <tr>
                <th scope="col">Wallet</th>
                <th scope="col" className="num">
                  Amount
                </th>
                <th scope="col" className="num">
                  Balance after
                </th>
              </tr>
            </thead>
            <tbody>
              {tx.entries.map((e, i) => (
                <tr key={i}>
                  <th scope="row">{e.walletName}</th>
                  <td className="num">{signedCredits(e.amount)}</td>
                  <td className="num">{credits(e.balanceAfter)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {isAdmin && !tx.reversedBy && tx.kind !== 'Reversal' && (
            <p>
              <button type="button" className="ghost" onClick={() => setReversing(true)}>
                Reverse…
              </button>
            </p>
          )}
          {reversing && <ReverseDialog tx={tx} onClose={() => setReversing(false)} onDone={(m) => toast('success', m)} />}
        </>
      )}
    </>
  );
}

function LoanDrawer({ id }: { id: number }) {
  const { loans } = useEconomy();
  const live: LoanDto | undefined = loans?.find((l) => l.id === id);
  const { data, error } = useLoaded(() => economyApi.loan(id), String(id), `${live?.state}:${live?.outstanding}`);
  const loan = live ?? data?.loan ?? null;
  return (
    <>
      {error && <p role="alert">{error}</p>}
      {loan && (
        <dl className="facts">
          <dt>Lender → borrower</dt>
          <dd>
            {loan.lender} → {loan.borrower}
          </dd>
          <dt>State</dt>
          <dd>
            <LoanStateChip loan={loan} />
          </dd>
          <dt>Principal</dt>
          <dd>{credits(loan.principal)} Cr</dd>
          <dt>Repay total</dt>
          <dd>
            {credits(loan.repayTotal)} Cr ({loan.interestBp / 100}% interest)
          </dd>
          <dt>Repaid / outstanding</dt>
          <dd>
            {credits(loan.repaid)} / {credits(loan.outstanding)}
            {loan.forgiven > 0 && ` (forgiven ${credits(loan.forgiven)})`}
          </dd>
          <dt>Auto-repay from income</dt>
          <dd>{loan.autoRepayPercent}%</dd>
          <dt>Due</dt>
          <dd>{loan.dueAt ? dateTimeOf(loan.dueAt) : 'not yet accepted'}</dd>
          {loan.closeReason && (
            <>
              <dt>Closed</dt>
              <dd>{loan.closeReason}</dd>
            </>
          )}
          {loan.memo && (
            <>
              <dt>Memo</dt>
              <dd>{loan.memo}</dd>
            </>
          )}
        </dl>
      )}
      {data && (
        <>
          <h3>Timeline</h3>
          <Events events={data.events} />
          <h3>Transactions</h3>
          <TxList txs={data.transactions} />
        </>
      )}
      {!data && !error && <p className="muted">Loading…</p>}
    </>
  );
}

function TradeDrawer({ id }: { id: number }) {
  const { trades } = useEconomy();
  const live: TradeOfferDto | undefined = trades?.find((t) => t.id === id);
  const { data, error } = useLoaded(() => economyApi.trade(id), String(id), `${live?.state}:${live?.version}`);
  const trade = live ?? data?.trade ?? null;
  return (
    <>
      {error && <p role="alert">{error}</p>}
      {trade && (
        <dl className="facts">
          <dt>State</dt>
          <dd>
            <TradeStateChip trade={trade} /> after {trade.queryAttempts} status queries
          </dd>
          <dt>{trade.initiator} gives</dt>
          <dd>{trade.initiatorGives.map(itemText).join(', ') || 'nothing'}</dd>
          <dt>{trade.counterparty} gives</dt>
          <dd>{trade.counterpartyGives.map(itemText).join(', ') || 'nothing'}</dd>
          <dt>Escrow</dt>
          <dd>{credits(trade.escrowAmount)} Cr</dd>
          <dt>Expires</dt>
          <dd>{dateTimeOf(trade.expiresAt)}</dd>
          {trade.reason && (
            <>
              <dt>Reason</dt>
              <dd>
                {trade.reason}
                {trade.detail ? `: ${trade.detail}` : ''}
              </dd>
            </>
          )}
          {trade.resolvedBy && (
            <>
              <dt>Resolved by</dt>
              <dd>{trade.resolvedBy}</dd>
            </>
          )}
        </dl>
      )}
      {data && (
        <>
          <h3>Timeline</h3>
          <Events events={data.events} />
          <h3>Transactions</h3>
          <TxList txs={data.transactions} />
        </>
      )}
      {!data && !error && <p className="muted">Loading…</p>}
    </>
  );
}

function titleOf(t: DrawerTarget): string {
  switch (t.type) {
    case 'wallet':
      return 'Wallet';
    case 'tx':
      return 'Transaction';
    case 'loan':
      return `Loan #${t.id}`;
    case 'trade':
      return `Trade #${t.id}`;
  }
}

/** Slide-in detail panel for a wallet, transaction, loan or trade. Closes on Escape. */
export function Drawer() {
  const { drawer, openDrawer, wallets } = useEconomy();
  const open = drawer !== null;
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !document.querySelector('.dialog-backdrop')) openDrawer(null);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, openDrawer]);
  if (!drawer) return null;
  let body: ReactNode = null;
  let title = titleOf(drawer);
  switch (drawer.type) {
    case 'wallet':
      title = `Wallet: ${wallets?.find((w) => w.kind === drawer.kind && w.ownerId === drawer.ownerId)?.ownerName ?? drawer.ownerId}`;
      body = <WalletDrawer key={`${drawer.kind}:${drawer.ownerId}`} kind={drawer.kind} ownerId={drawer.ownerId} />;
      break;
    case 'tx':
      body = <TxDrawer key={drawer.id} id={drawer.id} />;
      break;
    case 'loan':
      body = <LoanDrawer key={drawer.id} id={drawer.id} />;
      break;
    case 'trade':
      body = <TradeDrawer key={drawer.id} id={drawer.id} />;
      break;
  }
  return (
    <aside className="drawer" aria-label={`${title} details`}>
      <header className="drawer-head">
        <h2>{title}</h2>
        <button type="button" className="ghost" aria-label="Close details" onClick={() => openDrawer(null)}>
          ✕
        </button>
      </header>
      {body}
    </aside>
  );
}
