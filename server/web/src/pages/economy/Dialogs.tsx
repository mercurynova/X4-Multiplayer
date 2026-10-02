import { useState } from 'react';
import type { LedgerTxDto, LoanDto, TradeOfferDto, WalletDto } from '../../generated/generated';
import { ApiError } from '../../api/http';
import { economyApi } from './economyApi';
import { credits } from './format';
import { FieldError, ReasonDialog } from './ReasonDialog';
import { useEconomy } from './EconomyContext';

interface Common {
  onClose: () => void;
  onDone: (message: string) => void;
}

const overdrawable = (kind: string) => kind === 'Player' || kind === 'TeamShared';

/** What the wallets would hold after reversing `tx` (a reversal negates every leg), flagged when a player or shared wallet goes below zero. */
export function reversalPreview(tx: LedgerTxDto, wallets: WalletDto[] | null) {
  return tx.entries.map((e) => {
    const wallet = wallets?.find((w) => w.kind === e.walletKind && w.ownerId === e.walletOwnerId);
    const after = wallet ? wallet.balance - e.amount : null;
    return { name: e.walletName, kind: e.walletKind, before: wallet?.balance ?? null, after, negative: after !== null && after < 0 && overdrawable(e.walletKind) && (wallet?.balance ?? 0) >= 0 };
  });
}

export function ReverseDialog({ tx, onClose, onDone }: Common & { tx: LedgerTxDto }) {
  const { wallets, reload } = useEconomy();
  const [force, setForce] = useState(false);
  const [needsForce, setNeedsForce] = useState(false);
  const preview = reversalPreview(tx, wallets);
  const wouldOverdraw = preview.filter((p) => p.negative);
  const showForce = needsForce || wouldOverdraw.length > 0;
  return (
    <ReasonDialog
      title={`Reverse ${tx.kind} ${tx.id.slice(0, 8)}`}
      confirm="Reverse"
      intro={
        <>
          <p>Posts a second transaction that negates every leg. Both stay in the ledger and are linked.</p>
          <ul className="preview">
            {preview.map((p, i) => (
              <li key={i}>
                {p.name}: {p.before === null ? '?' : credits(p.before)} → {p.after === null ? '?' : credits(p.after)}
                {p.negative && <strong> (would go negative)</strong>}
              </li>
            ))}
          </ul>
          {wouldOverdraw.length > 0 && (
            <p>
              {wouldOverdraw.map((p) => `${p.name}'s balance would become ${credits(p.after ?? 0)}`).join('; ')}; tick force to allow.
            </p>
          )}
        </>
      }
      run={(reason) => economyApi.reverse(tx.id, { reason, force: force || null, returnAsset: null })}
      doneMessage="Transaction reversed."
      onError={(e) => {
        if (e instanceof ApiError && e.code === 'WouldOverdraw') setNeedsForce(true);
      }}
      onClose={onClose}
      onDone={(m) => {
        reload();
        onDone(m);
      }}
    >
      {showForce && (
        <label className="check">
          <input type="checkbox" checked={force} onChange={(e) => setForce(e.target.checked)} />
          force (allow a negative player or shared balance)
        </label>
      )}
    </ReasonDialog>
  );
}

export function AdjustDialog({ wallet, onClose, onDone }: Common & { wallet: WalletDto }) {
  const { reload } = useEconomy();
  const [amount, setAmount] = useState('');
  const [force, setForce] = useState(false);
  const [needsForce, setNeedsForce] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const parsed = amount.trim() === '' ? NaN : Number(amount.replace(/[,\s_]/g, ''));
  return (
    <ReasonDialog
      title={`Adjust ${wallet.ownerName}`}
      confirm="Adjust"
      intro={<p>Balance now {credits(wallet.balance)} Cr. A positive amount creates credits, a negative one removes them (booked against the World wallet).</p>}
      check={() => {
        const e: Record<string, string> = {};
        if (!Number.isInteger(parsed) || parsed === 0) e['amount'] = 'Enter a whole number of credits other than 0 (negative removes).';
        setErrors(e);
        return e;
      }}
      fieldErrors={errors}
      run={(reason) => economyApi.adjust(wallet.kind, wallet.ownerId, { amount: parsed, reason, force: force || null })}
      onError={(e) => {
        if (e instanceof ApiError && e.code === 'WouldOverdraw') setNeedsForce(true);
      }}
      doneMessage={`Adjusted ${wallet.ownerName} by ${amount}.`}
      onClose={onClose}
      onDone={(m) => {
        reload();
        onDone(m);
      }}
    >
      <label>
        Amount (credits, signed)
        <input
          inputMode="numeric"
          value={amount}
          onChange={(e) => setAmount(e.target.value)}
          aria-invalid={errors['amount'] ? true : undefined}
          autoComplete="off"
        />
        <FieldError text={errors['amount']} />
      </label>
      {(needsForce || (Number.isFinite(parsed) && parsed < 0 && wallet.balance + parsed < 0)) && (
        <label className="check">
          <input type="checkbox" checked={force} onChange={(e) => setForce(e.target.checked)} />
          force (allow {wallet.ownerName} to go below zero)
        </label>
      )}
    </ReasonDialog>
  );
}

export function FreezeDialog({ wallet, onClose, onDone }: Common & { wallet: WalletDto }) {
  const { reload } = useEconomy();
  const freezing = !wallet.frozen;
  return (
    <ReasonDialog
      title={`${freezing ? 'Freeze' : 'Unfreeze'} ${wallet.ownerName}`}
      confirm={freezing ? 'Freeze' : 'Unfreeze'}
      danger={freezing}
      intro={
        freezing ? (
          <p>A frozen wallet cannot send or receive player actions (donations, loans, trades). Game income and spending still book.</p>
        ) : (
          <p>Player actions on this wallet work again.</p>
        )
      }
      run={(reason) => economyApi.freeze(wallet.kind, wallet.ownerId, { frozen: freezing, reason })}
      doneMessage={`${wallet.ownerName} ${freezing ? 'frozen' : 'unfrozen'}.`}
      onClose={onClose}
      onDone={(m) => {
        reload();
        onDone(m);
      }}
    />
  );
}

export function ForgiveLoanDialog({ loan, onClose, onDone }: Common & { loan: LoanDto }) {
  const { reload } = useEconomy();
  return (
    <ReasonDialog
      title={`Forgive loan #${loan.id}`}
      confirm="Forgive"
      intro={
        <p>
          {loan.borrower} no longer owes the remaining {credits(loan.outstanding)} Cr to {loan.lender}. Nothing moves in the ledger.
        </p>
      }
      run={(reason) => economyApi.forgiveLoan(loan.id, { reason })}
      doneMessage={`Loan #${loan.id} forgiven.`}
      onClose={onClose}
      onDone={(m) => {
        reload();
        onDone(m);
      }}
    />
  );
}

export function CancelLoanDialog({ loan, onClose, onDone }: Common & { loan: LoanDto }) {
  const { reload } = useEconomy();
  const [reverse, setReverse] = useState(false);
  const [force, setForce] = useState(false);
  const [needsForce, setNeedsForce] = useState(false);
  const active = loan.state === 'Active' || loan.state === 'Overdue';
  return (
    <ReasonDialog
      title={`Cancel loan #${loan.id}`}
      confirm="Cancel loan"
      intro={
        active ? (
          <p>The loan closes as cancelled. Tick the box to also take the principal back from {loan.borrower} and return it to {loan.lender}.</p>
        ) : (
          <p>The offer is withdrawn and any escrow is refunded.</p>
        )
      }
      run={(reason) => economyApi.cancelLoan(loan.id, { reason, reverseDisbursement: reverse || null, force: force || null })}
      onError={(e) => {
        if (e instanceof ApiError && e.code === 'WouldOverdraw') setNeedsForce(true);
      }}
      doneMessage={`Loan #${loan.id} cancelled.`}
      onClose={onClose}
      onDone={(m) => {
        reload();
        onDone(m);
      }}
    >
      {active && (
        <label className="check">
          <input type="checkbox" checked={reverse} onChange={(e) => setReverse(e.target.checked)} />
          reverse the disbursement (take the principal back)
        </label>
      )}
      {(needsForce || reverse) && (
        <label className="check">
          <input type="checkbox" checked={force} onChange={(e) => setForce(e.target.checked)} />
          force (allow {loan.borrower} to go below zero)
        </label>
      )}
    </ReasonDialog>
  );
}

export function CancelTradeDialog({ trade, onClose, onDone }: Common & { trade: TradeOfferDto }) {
  const { reload } = useEconomy();
  return (
    <ReasonDialog
      title={`Cancel trade #${trade.id}`}
      confirm="Cancel trade"
      intro={<p>Escrowed credits are refunded and the locked assets are released.</p>}
      run={(reason) => economyApi.cancelTrade(trade.id, { reason })}
      doneMessage={`Trade #${trade.id} cancelled.`}
      onClose={onClose}
      onDone={(m) => {
        reload();
        onDone(m);
      }}
    />
  );
}

export function ResolveTradeDialog({
  trade,
  outcome,
  onClose,
  onDone,
}: Common & { trade: TradeOfferDto; outcome: 'complete' | 'refund' }) {
  const { reload } = useEconomy();
  const complete = outcome === 'complete';
  return (
    <ReasonDialog
      title={`Resolve trade #${trade.id}: ${outcome}`}
      confirm={complete ? 'Settle (complete)' : 'Refund'}
      intro={
        complete ? (
          <p>
            Treat the asset transfer as done: release the escrow ({credits(trade.escrowAmount)} Cr) to the receiving side and update ownership.
            Check in game first that the assets really moved.
          </p>
        ) : (
          <p>
            Treat the asset transfer as not done: refund the escrow ({credits(trade.escrowAmount)} Cr) to the payer and unlock the assets.
          </p>
        )
      }
      run={(reason) => economyApi.resolveTrade(trade.id, { outcome, reason })}
      doneMessage={`Trade #${trade.id} resolved: ${outcome}.`}
      onClose={onClose}
      onDone={(m) => {
        reload();
        onDone(m);
      }}
    />
  );
}
