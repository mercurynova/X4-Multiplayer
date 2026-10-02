import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ApiError } from '../../api/http';
import {
  AdminHubEvents as E,
  type EconomySummaryDto,
  type LedgerTxDto,
  type LoanDto,
  type TradeOfferDto,
  type WalletDto,
} from '../../generated/generated';
import { SNAPSHOT_EVENT } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { economyApi, emptyTxFilter, type TxFilter } from './economyApi';
import { economyGroup } from './economyGroup';
import { walletKey } from './format';

const PAGE = 100;

export type DrawerTarget =
  | { type: 'wallet'; kind: string; ownerId: number }
  | { type: 'tx'; id: string }
  | { type: 'loan'; id: number }
  | { type: 'trade'; id: number };

export interface EconomyData {
  summary: EconomySummaryDto | null;
  wallets: WalletDto[] | null;
  loans: LoanDto[] | null;
  trades: TradeOfferDto[] | null;
  txs: LedgerTxDto[] | null;
  txFilter: TxFilter;
  setTxFilter: (f: TxFilter) => void;
  hasMoreTxs: boolean;
  loadOlderTxs: () => Promise<void>;
  /** The economy does not exist until the session does (409 NoSession). */
  noSession: boolean;
  error: string | null;
  /** Re-reads everything (after a mutation, or a resubscribe). The hub pushes keep it current in between. */
  reload: () => void;
  drawer: DrawerTarget | null;
  openDrawer: (t: DrawerTarget | null) => void;
}

const Ctx = createContext<EconomyData | null>(null);

export function useEconomy(): EconomyData {
  const v = useContext(Ctx);
  if (!v) throw new Error('useEconomy must be used inside EconomyProvider');
  return v;
}

/** The REST answers are arrays; anything else (a stub, a proxy error page) is shown as empty rather than crashing the page. */
function asList<T>(v: T[]): T[] {
  return Array.isArray(v) ? v : [];
}

function upsert<T>(list: T[] | null, item: T, same: (a: T) => boolean): T[] {
  if (!list) return [item];
  const i = list.findIndex(same);
  if (i < 0) return [...list, item];
  const next = list.slice();
  next[i] = item;
  return next;
}

/** Does a pushed transaction belong in a list loaded with this filter? (The server filters the REST read; pushes are filtered here.) */
export function txMatches(tx: LedgerTxDto, f: TxFilter): boolean {
  if (f.kind && tx.kind !== f.kind) return false;
  if (f.actor.trim() && tx.actor !== f.actor.trim()) return false;
  if (f.wallet && !tx.entries.some((e) => walletKey(e.walletKind, e.walletOwnerId) === f.wallet.toLowerCase())) return false;
  return true;
}

/**
 * Shared economy state for the page and its drawers: REST snapshots plus the economy hub topic (WalletChanged, LedgerPosted,
 * LoanChanged, TradeChanged, EconomySummary). A (re)subscribe snapshot after the first one re-reads everything, since pushes may
 * have been missed while the connection was down.
 */
export function EconomyProvider({ children }: { children: ReactNode }) {
  const [summary, setSummary] = useState<EconomySummaryDto | null>(null);
  const [wallets, setWallets] = useState<WalletDto[] | null>(null);
  const [loans, setLoans] = useState<LoanDto[] | null>(null);
  const [trades, setTrades] = useState<TradeOfferDto[] | null>(null);
  const [txs, setTxs] = useState<LedgerTxDto[] | null>(null);
  const [txFilter, setTxFilterState] = useState<TxFilter>(emptyTxFilter);
  const [hasMoreTxs, setHasMoreTxs] = useState(false);
  const [noSession, setNoSession] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [drawer, openDrawer] = useState<DrawerTarget | null>(null);
  const [tick, setTick] = useState(0);
  const snapshots = useRef(0);

  const reload = useCallback(() => setTick((t) => t + 1), []);

  // Everything but the transactions.
  useEffect(() => {
    let cancelled = false;
    economyApi
      .summary()
      .then((s) => !cancelled && setSummary(s))
      .catch(() => undefined);
    Promise.all([economyApi.wallets(), economyApi.loans(), economyApi.trades()])
      .then(([w, l, t]) => {
        if (cancelled) return;
        setWallets(asList(w));
        setLoans(asList(l));
        setTrades(asList(t));
        setNoSession(false);
        setError(null);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        if (e instanceof ApiError && e.code === 'NoSession') {
          setNoSession(true);
          setError(null);
        } else setError(e instanceof Error ? e.message : 'Could not load the economy.');
      });
    return () => {
      cancelled = true;
    };
  }, [tick]);

  // Transactions, again when the filter changes.
  useEffect(() => {
    let cancelled = false;
    economyApi
      .transactions(txFilter, null, PAGE)
      .then((list) => {
        if (cancelled) return;
        setTxs(asList(list));
        setHasMoreTxs(asList(list).length >= PAGE);
      })
      .catch(() => {
        if (!cancelled) setTxs((cur) => cur ?? []);
      });
    return () => {
      cancelled = true;
    };
  }, [txFilter, tick]);

  const setTxFilter = useCallback((f: TxFilter) => setTxFilterState(f), []);

  const loadOlderTxs = useCallback(async () => {
    const last = txs?.[txs.length - 1];
    if (!last) return;
    const older = await economyApi.transactions(txFilter, last.id, PAGE);
    setTxs((cur) => {
      const seen = new Set((cur ?? []).map((t) => t.id));
      return [...(cur ?? []), ...older.filter((t) => !seen.has(t.id))];
    });
    setHasMoreTxs(older.length >= PAGE);
  }, [txs, txFilter]);

  useHubGroup(economyGroup, (event, payload) => {
    switch (event) {
      case SNAPSHOT_EVENT:
        setSummary(payload as EconomySummaryDto);
        snapshots.current += 1;
        if (snapshots.current > 1) reload();
        break;
      case E.EconomySummary:
        setSummary(payload as EconomySummaryDto);
        break;
      case E.WalletChanged: {
        const w = payload as WalletDto;
        setWallets((cur) => upsert(cur, w, (x) => x.kind === w.kind && x.ownerId === w.ownerId));
        break;
      }
      case E.LedgerPosted: {
        const tx = payload as LedgerTxDto;
        setTxs((cur) => {
          if (!cur) return cur;
          // A reversal links back to its original: keep the original's "reversed" marker current.
          const linked = tx.reverses ? cur.map((t) => (t.id === tx.reverses ? { ...t, reversedBy: tx.id } : t)) : cur;
          if (!txMatches(tx, txFilter) || linked.some((t) => t.id === tx.id)) return linked;
          return [tx, ...linked];
        });
        break;
      }
      case E.LoanChanged: {
        const l = payload as LoanDto;
        setLoans((cur) => upsert(cur, l, (x) => x.id === l.id));
        break;
      }
      case E.TradeChanged: {
        const t = payload as TradeOfferDto;
        setTrades((cur) => upsert(cur, t, (x) => x.id === t.id));
        break;
      }
    }
  });

  const value = useMemo<EconomyData>(
    () => ({
      summary, wallets, loans, trades, txs, txFilter, setTxFilter, hasMoreTxs, loadOlderTxs, noSession, error, reload, drawer, openDrawer,
    }),
    [summary, wallets, loans, trades, txs, txFilter, setTxFilter, hasMoreTxs, loadOlderTxs, noSession, error, reload, drawer],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
