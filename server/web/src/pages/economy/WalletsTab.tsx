import { useMemo, useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import type { WalletDto } from '../../generated/generated';
import { AdjustDialog, FreezeDialog } from './Dialogs';
import { useEconomy } from './EconomyContext';
import { credits, WALLET_KINDS } from './format';

const kindLabel: Record<string, string> = {
  Player: 'player',
  TeamShared: 'team shared',
  TeamPool: 'team pool',
  Escrow: 'escrow',
  World: 'world',
};

export function WalletFlags({ wallet }: { wallet: WalletDto }) {
  return (
    <>
      {wallet.frozen && (
        <span className="flag flag-frozen" title={wallet.frozenReason ?? undefined}>
          FROZEN
        </span>
      )}
      {wallet.overdrawn && <span className="flag flag-overdrawn">OVERDRAWN</span>}
    </>
  );
}

export function WalletsTab() {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { wallets, openDrawer } = useEconomy();
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('');
  const [adjust, setAdjust] = useState<WalletDto | null>(null);
  const [freeze, setFreeze] = useState<WalletDto | null>(null);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (wallets ?? [])
      .filter((w) => (kind === '' || w.kind === kind) && (q === '' || w.ownerName.toLowerCase().includes(q)))
      .sort((a, b) => a.kind.localeCompare(b.kind) || a.ownerName.localeCompare(b.ownerName));
  }, [wallets, query, kind]);

  return (
    <section aria-label="Wallets">
      <div className="page-head">
        <input
          type="search"
          className="search"
          placeholder="Search wallets"
          aria-label="Search wallets"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <label className="inline">
          <span className="visually-hidden">Wallet kind</span>
          <select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Wallet kind">
            <option value="">All kinds</option>
            {WALLET_KINDS.map((k) => (
              <option key={k} value={k}>
                {kindLabel[k]}
              </option>
            ))}
          </select>
        </label>
      </div>
      {wallets !== null && shown.length === 0 && <p className="muted">No wallets match.</p>}
      {shown.length > 0 && (
        <div className="table-wrap">
          <table className="data">
            <caption className="visually-hidden">Wallets</caption>
            <thead>
              <tr>
                <th scope="col">Wallet</th>
                <th scope="col">Kind</th>
                <th scope="col" className="num">
                  Balance
                </th>
                <th scope="col">Flags</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((w) => (
                <tr key={`${w.kind}:${w.ownerId}`}>
                  <th scope="row">{w.ownerName}</th>
                  <td>{kindLabel[w.kind] ?? w.kind}</td>
                  <td className="num">{credits(w.balance)}</td>
                  <td>
                    <WalletFlags wallet={w} />
                  </td>
                  <td>
                    <div className="actions">
                      <button type="button" className="ghost" aria-label={`Ledger ${w.ownerName}`} onClick={() => openDrawer({ type: 'wallet', kind: w.kind, ownerId: w.ownerId })}>
                        Ledger
                      </button>
                      {isAdmin && w.kind !== 'World' && (
                        <>
                          <button type="button" className="ghost" aria-label={`Adjust ${w.ownerName}`} onClick={() => setAdjust(w)}>
                            Adjust…
                          </button>
                          <button type="button" className="ghost" aria-label={`${w.frozen ? 'Unfreeze' : 'Freeze'} ${w.ownerName}`} onClick={() => setFreeze(w)}>
                            {w.frozen ? 'Unfreeze' : 'Freeze'}
                          </button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {adjust && <AdjustDialog wallet={adjust} onClose={() => setAdjust(null)} onDone={(m) => toast('success', m)} />}
      {freeze && <FreezeDialog wallet={freeze} onClose={() => setFreeze(null)} onDone={(m) => toast('success', m)} />}
    </section>
  );
}
