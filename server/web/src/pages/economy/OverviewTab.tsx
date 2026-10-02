import { useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import { economyApi, describeEconomyError } from './economyApi';
import { useEconomy } from './EconomyContext';
import { credits, dateTimeOf } from './format';

export function modeText(mode: string): string {
  return mode.replace(/([a-z])([A-Z])/g, '$1 $2').toUpperCase();
}

function Tile({ label, value, sub, tone }: { label: string; value: string; sub?: string; tone?: 'warn' | 'danger' }) {
  return (
    <div className={`tile${tone ? ` tile-${tone}` : ''}`}>
      <div className="tile-label">{label}</div>
      <div className="tile-value">{value}</div>
      {sub && <div className="tile-sub">{sub}</div>}
    </div>
  );
}

export function AuditButtons() {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { summary, reload } = useEconomy();
  const [busy, setBusy] = useState(false);
  if (!isAdmin) return null;
  const run = (acknowledge: boolean) => {
    setBusy(true);
    economyApi
      .audit(acknowledge)
      .then((r) => {
        toast(r.ok ? 'success' : 'error', r.ok ? 'Audit passed.' : `Audit found ${r.violations.length} violation(s).`);
        reload();
      })
      .catch((e: unknown) => toast('error', describeEconomyError(e)))
      .finally(() => setBusy(false));
  };
  return (
    <span className="actions">
      <button type="button" className="ghost" disabled={busy} onClick={() => run(false)}>
        Run audit now
      </button>
      {summary?.economyFrozen && (
        <button type="button" className="danger" disabled={busy} onClick={() => run(true)}>
          Acknowledge and unfreeze
        </button>
      )}
    </span>
  );
}

export function OverviewTab() {
  const { summary } = useEconomy();
  if (!summary) return <p className="muted">Loading the economy…</p>;
  const audit = summary.lastAudit;
  const auditKnown = audit && audit.at && !audit.at.startsWith('0001');
  return (
    <section aria-label="Overview">
      <div className="tiles">
        <Tile label="Money supply" value={`${credits(summary.moneySupply)} Cr`} />
        <Tile label="In escrow" value={`${credits(summary.inEscrow)} Cr`} />
        <Tile label="Outstanding debt" value={`${credits(summary.outstandingDebt)} Cr`} sub={`${summary.openLoans} open loans`} />
        <Tile label="Overdue loans" value={String(summary.overdueLoans)} tone={summary.overdueLoans > 0 ? 'warn' : undefined} />
        <Tile label="Open trades" value={String(summary.openTrades)} />
        <Tile label="In-doubt trades" value={String(summary.inDoubtTrades)} tone={summary.inDoubtTrades > 0 ? 'danger' : undefined} />
        <Tile label="Frozen wallets" value={String(summary.frozenWallets)} tone={summary.frozenWallets > 0 ? 'warn' : undefined} />
        <Tile
          label="Credit mode"
          value={summary.creditMode === 'Auto' ? `Auto → ${modeText(summary.effectiveCreditMode)}` : modeText(summary.creditMode)}
          sub={summary.migrationPending ? 'migration pending' : `applied: ${modeText(summary.appliedCreditMode)}`}
          tone={summary.migrationPending ? 'warn' : undefined}
        />
      </div>
      <div className="panel">
        <h2>Auditor</h2>
        <p>
          {auditKnown ? (
            audit.ok ? (
              <span className="presence-on">All balances reconcile (checked {dateTimeOf(audit.at)}).</span>
            ) : (
              <span role="alert">Violations found at {dateTimeOf(audit.at)}.</span>
            )
          ) : (
            <span className="muted">No audit has run yet.</span>
          )}
        </p>
        {auditKnown && !audit.ok && (
          <ul>
            {audit.violations.map((v, i) => (
              <li key={i}>{v}</li>
            ))}
          </ul>
        )}
        <AuditButtons />
      </div>
    </section>
  );
}
