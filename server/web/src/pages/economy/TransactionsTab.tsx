import { useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import type { LedgerTxDto } from '../../generated/generated';
import { ReverseDialog } from './Dialogs';
import { economyApi } from './economyApi';
import { useEconomy } from './EconomyContext';
import { credits, signedCredits, timeOf, TX_KINDS, walletKey } from './format';

/** "Bob → Dan" from the legs: debited wallets to credited wallets. */
export function parties(tx: LedgerTxDto): { from: string; to: string; amount: number } {
  const from = tx.entries.filter((e) => e.amount < 0).map((e) => e.walletName);
  const to = tx.entries.filter((e) => e.amount > 0).map((e) => e.walletName);
  const amount = tx.entries.filter((e) => e.amount > 0).reduce((s, e) => s + e.amount, 0);
  return { from: [...new Set(from)].join(', '), to: [...new Set(to)].join(', '), amount };
}

export function TxRow({ tx }: { tx: LedgerTxDto }) {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { openDrawer } = useEconomy();
  const [reversing, setReversing] = useState(false);
  const p = parties(tx);
  const reversed = tx.reversedBy !== null;
  return (
    <tr className={reversed ? 'tx-reversed' : undefined}>
      <td className="mono">{timeOf(tx.at)}</td>
      <td>
        {tx.kind}
        {tx.reverses && <span className="flag flag-info" title={`reverses ${tx.reverses}`}> ↺ reversal</span>}
        {reversed && <span className="flag flag-warn" title={`reversed by ${tx.reversedBy}`}> reversed</span>}
      </td>
      <td>
        {p.from || '—'} → {p.to || '—'}
        {tx.refType && <span className="muted"> ({tx.refType} #{tx.refId})</span>}
      </td>
      <td className="num">{credits(p.amount)}</td>
      <td className="muted">{tx.actor}</td>
      <td>
        <div className="actions">
          <button type="button" className="ghost" aria-label={`Details ${tx.id}`} onClick={() => openDrawer({ type: 'tx', id: tx.id })}>
            ⋯
          </button>
          {isAdmin && !reversed && tx.kind !== 'Reversal' && (
            <button type="button" className="ghost" aria-label={`Reverse ${tx.id}`} onClick={() => setReversing(true)}>
              Reverse…
            </button>
          )}
        </div>
        {reversing && <ReverseDialog tx={tx} onClose={() => setReversing(false)} onDone={(m) => toast('success', m)} />}
      </td>
    </tr>
  );
}

export function TransactionsTab() {
  const { isAdmin } = useAuth();
  const { txs, txFilter, setTxFilter, wallets, hasMoreTxs, loadOlderTxs } = useEconomy();
  const [loadingMore, setLoadingMore] = useState(false);

  return (
    <section aria-label="Transactions">
      <div className="page-head">
        <h2 className="head-title">Transactions (live)</h2>
        <label className="inline">
          Wallet
          <select value={txFilter.wallet} onChange={(e) => setTxFilter({ ...txFilter, wallet: e.target.value })}>
            <option value="">All wallets</option>
            {(wallets ?? []).map((w) => (
              <option key={`${w.kind}:${w.ownerId}`} value={walletKey(w.kind, w.ownerId)}>
                {w.ownerName}
              </option>
            ))}
          </select>
        </label>
        <label className="inline">
          Kind
          <select value={txFilter.kind} onChange={(e) => setTxFilter({ ...txFilter, kind: e.target.value })}>
            <option value="">All kinds</option>
            {TX_KINDS.map((k) => (
              <option key={k} value={k}>
                {k}
              </option>
            ))}
          </select>
        </label>
        <label className="inline">
          Actor
          <input
            value={txFilter.actor}
            onChange={(e) => setTxFilter({ ...txFilter, actor: e.target.value })}
            placeholder="e.g. admin:jack"
            autoComplete="off"
          />
        </label>
        {isAdmin && (
          <a className="button-link" href={economyApi.ledgerCsvUrl} download="ledger.csv">
            ⤓ CSV
          </a>
        )}
      </div>
      {txs !== null && txs.length === 0 && <p className="muted">No transactions match.</p>}
      {txs !== null && txs.length > 0 && (
        <div className="table-wrap">
          <table className="data">
            <caption className="visually-hidden">Transactions</caption>
            <thead>
              <tr>
                <th scope="col">Time</th>
                <th scope="col">Kind</th>
                <th scope="col">From → to</th>
                <th scope="col" className="num">
                  Amount
                </th>
                <th scope="col">Actor</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {txs.map((tx) => (
                <TxRow key={tx.id} tx={tx} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      {hasMoreTxs && (
        <p>
          <button
            type="button"
            className="ghost"
            disabled={loadingMore}
            onClick={() => {
              setLoadingMore(true);
              loadOlderTxs().finally(() => setLoadingMore(false));
            }}
          >
            Load older
          </button>
        </p>
      )}
    </section>
  );
}

/** One leg line for the drawer: "Bob -50,000 (balance 862,400)". */
export function legText(amount: number, balanceAfter: number): string {
  return `${signedCredits(amount)} (balance ${credits(balanceAfter)})`;
}
