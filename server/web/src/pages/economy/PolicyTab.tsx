import { useEffect, useState, type FormEvent } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import type { EconomyPolicyDto, EconomyPolicyPatch, MigrationPreviewDto } from '../../generated/generated';
import { problemToFormErrors } from '../../lib/problem';
import { Dialog } from '../players/Dialog';
import { describeEconomyError, economyApi } from './economyApi';
import { useEconomy } from './EconomyContext';
import { credits } from './format';

type Draft = Record<string, string | boolean>;

interface SelectField { key: keyof EconomyPolicyDto; label: string; options: readonly string[]; kind: 'select' }
interface BoolField { key: keyof EconomyPolicyDto; label: string; kind: 'bool' }
interface NumField { key: keyof EconomyPolicyDto; label: string; kind: 'number'; hint?: string }
type Field = SelectField | BoolField | NumField;

const scopes = ['Off', 'Teammates', 'Allied', 'Anyone'] as const;

const groups: { title: string; fields: Field[] }[] = [
  {
    title: 'Credits',
    fields: [
      { key: 'creditMode', label: 'Credit mode', kind: 'select', options: ['Auto', 'PerPlayer', 'Shared'] },
      { key: 'startingCredits', label: 'Starting credits', kind: 'number' },
      { key: 'teamPoolEnabled', label: 'Team pool', kind: 'bool' },
      { key: 'poolWithdrawPolicy', label: 'Pool withdraw', kind: 'select', options: ['AnyMember', 'LeaderOnly', 'Disabled'] },
      { key: 'poolWithdrawDailyLimitPerPlayer', label: 'Pool daily limit per player', kind: 'number', hint: '0 = unlimited' },
      { key: 'sharedWalletSpend', label: 'Shared-wallet spend', kind: 'select', options: ['AnyMember', 'LeaderOnly'] },
    ],
  },
  {
    title: 'Who may do what',
    fields: [
      { key: 'donateScope', label: 'Donate', kind: 'select', options: scopes },
      { key: 'allowAlliedTransfers', label: 'Teammate transfers reach allies', kind: 'bool' },
      { key: 'loanScope', label: 'Loans', kind: 'select', options: scopes },
      { key: 'tradeScope', label: 'Trades', kind: 'select', options: scopes },
      { key: 'tradeShipsEnabled', label: 'Ships may be traded', kind: 'bool' },
      { key: 'tradeRequiresProximity', label: 'Trade needs same sector', kind: 'bool' },
    ],
  },
  {
    title: 'Limits and timing',
    fields: [
      { key: 'maxSingleTransfer', label: 'Max transfer', kind: 'number' },
      { key: 'maxLoanPrincipal', label: 'Max loan', kind: 'number' },
      { key: 'maxLoanInterestBp', label: 'Max interest (basis points)', kind: 'number', hint: '100 bp = 1 %' },
      { key: 'maxOpenLoansPerPlayer', label: 'Open loans per player', kind: 'number' },
      { key: 'maxOpenTradesPerPlayer', label: 'Open trades per player', kind: 'number' },
      { key: 'offerDefaultTtlMinutes', label: 'Offer lifetime (minutes)', kind: 'number' },
      { key: 'tradeExecuteTimeoutSeconds', label: 'Trade execute timeout (s)', kind: 'number' },
      { key: 'tradeQueryIntervalSeconds', label: 'Trade query interval (s)', kind: 'number' },
      { key: 'auditIntervalSeconds', label: 'Audit interval (s)', kind: 'number' },
    ],
  },
];

const allFields = groups.flatMap((g) => g.fields);

function toDraft(p: EconomyPolicyDto): Draft {
  const d: Draft = {};
  for (const f of allFields) {
    const v = p[f.key];
    d[f.key] = f.kind === 'bool' ? Boolean(v) : String(v);
  }
  return d;
}

/** The fields whose draft value differs from the saved policy, as a PATCH body. */
function diff(policy: EconomyPolicyDto, draft: Draft): { patch: Partial<EconomyPolicyPatch>; errors: Record<string, string> } {
  const patch: Record<string, unknown> = {};
  const errors: Record<string, string> = {};
  for (const f of allFields) {
    const v = draft[f.key];
    if (f.kind === 'number') {
      const text = String(v).trim().replace(/[,_\s]/g, '');
      const n = Number(text);
      if (text === '' || !Number.isInteger(n)) {
        errors[f.key] = 'Enter a whole number.';
        continue;
      }
      if (n !== policy[f.key]) patch[f.key] = n;
    } else if (v !== (f.kind === 'bool' ? Boolean(policy[f.key]) : String(policy[f.key]))) {
      patch[f.key] = v;
    }
  }
  return { patch: patch as Partial<EconomyPolicyPatch>, errors };
}

function MigrationPreview({ preview }: { preview: MigrationPreviewDto }) {
  return (
    <div>
      <p>
        Switching from <strong>{preview.from}</strong> to <strong>{preview.to}</strong> ({preview.kind}) moves {credits(preview.totalMoved)} Cr
        between wallets.
      </p>
      {preview.teamMoves.length > 0 && (
        <ul>
          {preview.teamMoves.map((m, i) => (
            <li key={i}>{m}</li>
          ))}
        </ul>
      )}
      <div className="table-wrap">
        <table className="data">
          <caption className="visually-hidden">Balances after the migration</caption>
          <thead>
            <tr>
              <th scope="col">Wallet</th>
              <th scope="col" className="num">
                Before
              </th>
              <th scope="col" className="num">
                After
              </th>
            </tr>
          </thead>
          <tbody>
            {preview.changes.map((c) => (
              <tr key={`${c.kind}:${c.ownerId}`}>
                <th scope="row">{c.ownerName}</th>
                <td className="num">{credits(c.before)}</td>
                <td className="num">{credits(c.after)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export function PolicyTab() {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { reload } = useEconomy();
  const [policy, setPolicy] = useState<EconomyPolicyDto | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [reason, setReason] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState<{ preview: MigrationPreviewDto; message: string } | null>(null);

  useEffect(() => {
    let cancelled = false;
    economyApi
      .policy()
      .then((p) => {
        if (cancelled) return;
        setPolicy(p);
        setDraft(toDraft(p));
      })
      .catch((e: unknown) => !cancelled && setFormError(describeEconomyError(e)));
    return () => {
      cancelled = true;
    };
  }, []);

  if (!policy || !draft) return formError ? <p role="alert">{formError}</p> : <p className="muted">Loading the policy…</p>;

  const { patch, errors: local } = diff(policy, draft);
  const changed = Object.keys(patch).length;

  const send = (withConfirm: boolean) => {
    setBusy(true);
    setFormError(null);
    economyApi
      .patchPolicy({ ...patch, reason: reason.trim() || null, confirm: withConfirm ? true : null })
      .then((r) => {
        if (r.status === 'confirm') {
          setConfirm({ preview: r.preview, message: r.message });
          return;
        }
        setConfirm(null);
        setPolicy(r.policy);
        setDraft(toDraft(r.policy));
        setReason('');
        setErrors({});
        toast('success', 'Economy policy saved.');
        reload();
      })
      .catch((e: unknown) => {
        setConfirm(null);
        const fe = problemToFormErrors(e);
        setErrors(fe.fields);
        setFormError(Object.keys(fe.fields).length > 0 ? 'Some fields were rejected; nothing was changed.' : describeEconomyError(e));
      })
      .finally(() => setBusy(false));
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setErrors(local);
    if (Object.keys(local).length > 0 || changed === 0) return;
    send(false);
  };

  const shownErrors = { ...errors };
  return (
    <section aria-label="Policy">
      <form onSubmit={submit} noValidate className="policy">
        {groups.map((g) => (
          <fieldset key={g.title} className="panel" disabled={!isAdmin}>
            <legend>{g.title}</legend>
            <div className="policy-grid">
              {g.fields.map((f) => {
                const err = shownErrors[f.key];
                if (f.kind === 'bool') {
                  return (
                    <label key={f.key} className="check">
                      <input
                        type="checkbox"
                        checked={Boolean(draft[f.key])}
                        onChange={(e) => setDraft({ ...draft, [f.key]: e.target.checked })}
                      />
                      {f.label}
                    </label>
                  );
                }
                return (
                  <label key={f.key}>
                    {f.label}
                    {f.kind === 'select' ? (
                      <select value={String(draft[f.key])} onChange={(e) => setDraft({ ...draft, [f.key]: e.target.value })}>
                        {f.options.map((o) => (
                          <option key={o} value={o}>
                            {o}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <input
                        inputMode="numeric"
                        value={String(draft[f.key])}
                        onChange={(e) => setDraft({ ...draft, [f.key]: e.target.value })}
                        aria-invalid={err || local[f.key] ? true : undefined}
                        autoComplete="off"
                      />
                    )}
                    {f.kind === 'number' && f.hint && <span className="muted">{f.hint}</span>}
                    {(err || local[f.key]) && <span className="field-error">{err ?? local[f.key]}</span>}
                  </label>
                );
              })}
            </div>
            {g.title === 'Credits' && policy.creditMode === 'Auto' && (
              <p className="muted">Auto currently resolves to {policy.effectiveCreditMode}.</p>
            )}
          </fieldset>
        ))}
        {formError && <p role="alert">{formError}</p>}
        {isAdmin && (
          <div className="row">
            <label className="grow">
              Reason (optional, goes to the audit log)
              <input value={reason} onChange={(e) => setReason(e.target.value)} autoComplete="off" />
            </label>
            <button type="submit" disabled={busy || changed === 0}>
              Save policy{changed > 0 ? ` (${changed} change${changed === 1 ? '' : 's'})` : ''}
            </button>
            <button type="button" className="ghost" disabled={changed === 0} onClick={() => setDraft(toDraft(policy))}>
              Discard
            </button>
          </div>
        )}
      </form>
      {confirm && (
        <Dialog title="Confirm credit mode change" onClose={() => setConfirm(null)}>
          <p className="muted">{confirm.message}</p>
          <MigrationPreview preview={confirm.preview} />
          <div className="dialog-buttons">
            <button type="button" className="ghost" onClick={() => setConfirm(null)}>
              Cancel
            </button>
            <button type="button" className="danger" disabled={busy} onClick={() => send(true)}>
              Apply and move balances
            </button>
          </div>
        </Dialog>
      )}
    </section>
  );
}
