import { useMemo, useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import type { TradeItemDto, TradeOfferDto } from '../../generated/generated';
import { CancelTradeDialog, ResolveTradeDialog } from './Dialogs';
import { useEconomy } from './EconomyContext';
import { CANCELLABLE_TRADE_STATES, credits, OPEN_TRADE_STATES } from './format';

const TRADE_STATES = ['Proposed', 'Countered', 'Accepted', 'Escrowed', 'Transferring', 'InDoubt', 'Completed', 'RolledBack', 'Cancelled', 'Expired', 'Rejected'];

export function itemText(i: TradeItemDto): string {
  switch (i.kind) {
    case 'Credits':
      return `${credits(i.amount)} Cr`;
    case 'Ware':
      return `${credits(i.amount)} × ware ${i.wareRef}`;
    case 'Ship':
      return `ship #${i.asset}`;
    case 'Station':
      return `station #${i.asset}`;
    default:
      return i.kind;
  }
}

const gives = (who: string, items: TradeItemDto[]) => `${who}: ${items.length === 0 ? 'nothing' : items.map(itemText).join(', ')}`;

export function TradeStateChip({ trade }: { trade: TradeOfferDto }) {
  if (trade.state === 'InDoubt') return <span className="flag flag-danger">⚠ IN DOUBT</span>;
  return <span className="flag flag-info">{trade.state}</span>;
}

export function TradesTab() {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { trades, openDrawer } = useEconomy();
  const [state, setState] = useState('open');
  const [cancel, setCancel] = useState<TradeOfferDto | null>(null);
  const [resolve, setResolve] = useState<{ trade: TradeOfferDto; outcome: 'complete' | 'refund' } | null>(null);

  const shown = useMemo(
    () =>
      (trades ?? [])
        .filter((t) => (state === 'all' ? true : state === 'open' ? OPEN_TRADE_STATES.includes(t.state) : t.state === state))
        .sort((a, b) => Number(b.state === 'InDoubt') - Number(a.state === 'InDoubt') || b.id - a.id),
    [trades, state],
  );

  return (
    <section aria-label="Trades">
      <div className="page-head">
        <label className="inline">
          State
          <select value={state} onChange={(e) => setState(e.target.value)}>
            <option value="open">open</option>
            <option value="all">all</option>
            {TRADE_STATES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </label>
      </div>
      {trades !== null && shown.length === 0 && <p className="muted">No trades match.</p>}
      {shown.length > 0 && (
        <div className="table-wrap">
          <table className="data">
            <caption className="visually-hidden">Trades</caption>
            <thead>
              <tr>
                <th scope="col">#</th>
                <th scope="col">Proposer gives</th>
                <th scope="col">Counterparty gives</th>
                <th scope="col">State</th>
                <th scope="col" className="num">
                  Attempts
                </th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((t) => (
                <tr key={t.id}>
                  <td>{t.id}</td>
                  <td>{gives(t.initiator, t.initiatorGives)}</td>
                  <td>{gives(t.counterparty, t.counterpartyGives)}</td>
                  <td>
                    <TradeStateChip trade={t} />
                    {t.reversed && <span className="flag flag-warn">reversed</span>}
                  </td>
                  <td className="num">{t.queryAttempts}</td>
                  <td>
                    <div className="actions">
                      <button type="button" className="ghost" aria-label={`Details trade ${t.id}`} onClick={() => openDrawer({ type: 'trade', id: t.id })}>
                        ⋯
                      </button>
                      {isAdmin && t.state === 'InDoubt' && (
                        <>
                          <button type="button" className="ghost" aria-label={`Settle trade ${t.id}`} onClick={() => setResolve({ trade: t, outcome: 'complete' })}>
                            Resolve: complete…
                          </button>
                          <button type="button" className="ghost" aria-label={`Refund trade ${t.id}`} onClick={() => setResolve({ trade: t, outcome: 'refund' })}>
                            Resolve: refund…
                          </button>
                        </>
                      )}
                      {isAdmin && CANCELLABLE_TRADE_STATES.includes(t.state) && (
                        <button type="button" className="ghost" aria-label={`Cancel trade ${t.id}`} onClick={() => setCancel(t)}>
                          Cancel…
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {cancel && <CancelTradeDialog trade={cancel} onClose={() => setCancel(null)} onDone={(m) => toast('success', m)} />}
      {resolve && (
        <ResolveTradeDialog trade={resolve.trade} outcome={resolve.outcome} onClose={() => setResolve(null)} onDone={(m) => toast('success', m)} />
      )}
    </section>
  );
}
