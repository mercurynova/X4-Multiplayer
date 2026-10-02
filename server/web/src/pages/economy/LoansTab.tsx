import { useMemo, useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import type { LoanDto } from '../../generated/generated';
import { CancelLoanDialog, ForgiveLoanDialog } from './Dialogs';
import { useEconomy } from './EconomyContext';
import { credits, dateTimeOf, OPEN_LOAN_STATES } from './format';

const LOAN_STATES = ['Offered', 'Active', 'Overdue', 'Repaid', 'Declined', 'Withdrawn', 'Expired', 'Forgiven', 'Cancelled'];

export function LoanStateChip({ loan }: { loan: LoanDto }) {
  if (loan.overdue || loan.state === 'Overdue') return <span className="flag flag-danger">OVERDUE</span>;
  return <span className="flag flag-info">{loan.state}</span>;
}

export function LoansTab() {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { loans, openDrawer } = useEconomy();
  const [state, setState] = useState('open');
  const [forgive, setForgive] = useState<LoanDto | null>(null);
  const [cancel, setCancel] = useState<LoanDto | null>(null);

  const shown = useMemo(
    () =>
      (loans ?? [])
        .filter((l) => (state === 'all' ? true : state === 'open' ? OPEN_LOAN_STATES.includes(l.state) || l.overdue : l.state === state))
        .sort((a, b) => Number(b.overdue) - Number(a.overdue) || b.id - a.id),
    [loans, state],
  );

  return (
    <section aria-label="Loans">
      <div className="page-head">
        <label className="inline">
          State
          <select value={state} onChange={(e) => setState(e.target.value)}>
            <option value="open">open</option>
            <option value="all">all</option>
            {LOAN_STATES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </label>
      </div>
      {loans !== null && shown.length === 0 && <p className="muted">No loans match.</p>}
      {shown.length > 0 && (
        <div className="table-wrap">
          <table className="data">
            <caption className="visually-hidden">Loans</caption>
            <thead>
              <tr>
                <th scope="col">#</th>
                <th scope="col">Lender → borrower</th>
                <th scope="col" className="num">
                  Principal
                </th>
                <th scope="col" className="num">
                  Int.
                </th>
                <th scope="col">Due</th>
                <th scope="col" className="num">
                  Outstanding
                </th>
                <th scope="col">State</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((l) => {
                const live = l.state === 'Active' || l.state === 'Overdue';
                const open = live || l.state === 'Offered';
                return (
                  <tr key={l.id}>
                    <td>{l.id}</td>
                    <td>
                      {l.lender} → {l.borrower}
                    </td>
                    <td className="num">{credits(l.principal)}</td>
                    <td className="num">{l.interestBp / 100}%</td>
                    <td>{l.dueAt ? dateTimeOf(l.dueAt) : '—'}</td>
                    <td className="num">{credits(l.outstanding)}</td>
                    <td>
                      <LoanStateChip loan={l} />
                    </td>
                    <td>
                      <div className="actions">
                        <button type="button" className="ghost" aria-label={`Details loan ${l.id}`} onClick={() => openDrawer({ type: 'loan', id: l.id })}>
                          ⋯
                        </button>
                        {isAdmin && live && (
                          <button type="button" className="ghost" aria-label={`Forgive loan ${l.id}`} onClick={() => setForgive(l)}>
                            Forgive…
                          </button>
                        )}
                        {isAdmin && open && (
                          <button type="button" className="ghost" aria-label={`Cancel loan ${l.id}`} onClick={() => setCancel(l)}>
                            Cancel…
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {forgive && <ForgiveLoanDialog loan={forgive} onClose={() => setForgive(null)} onDone={(m) => toast('success', m)} />}
      {cancel && <CancelLoanDialog loan={cancel} onClose={() => setCancel(null)} onDone={(m) => toast('success', m)} />}
    </section>
  );
}
