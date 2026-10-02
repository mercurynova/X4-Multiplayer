import { NavLink, useParams } from 'react-router';
import { EconomyProvider, useEconomy } from './EconomyContext';
import { Drawer } from './Drawer';
import { LoansTab } from './LoansTab';
import { AuditButtons, modeText, OverviewTab } from './OverviewTab';
import { PolicyTab } from './PolicyTab';
import { TradesTab } from './TradesTab';
import { TransactionsTab } from './TransactionsTab';
import { WalletsTab } from './WalletsTab';
import { credits } from './format';
import '../players/players.css';
import './economy.css';

const tabs = [
  { id: 'overview', title: 'Overview' },
  { id: 'wallets', title: 'Wallets' },
  { id: 'transactions', title: 'Transactions' },
  { id: 'loans', title: 'Loans' },
  { id: 'trades', title: 'Trades' },
  { id: 'policy', title: 'Policy' },
] as const;

type TabId = (typeof tabs)[number]['id'];

function Header() {
  const { summary } = useEconomy();
  return (
    <header className="page-head">
      <h1>Economy</h1>
      {summary && (
        <p className="econ-strip muted" aria-label="Economy summary">
          mode: {summary.creditMode === 'Auto' ? `Auto → ${modeText(summary.effectiveCreditMode)}` : modeText(summary.creditMode)} · supply{' '}
          {credits(summary.moneySupply)} Cr · escrow {credits(summary.inEscrow)}
          {summary.inDoubtTrades > 0 && <strong className="warn-text"> · ⚠ {summary.inDoubtTrades} in doubt</strong>}
        </p>
      )}
    </header>
  );
}

function Banners() {
  const { summary } = useEconomy();
  if (!summary) return null;
  const breach = summary.economyFrozen || (summary.lastAudit && !summary.lastAudit.ok && !summary.lastAudit.at.startsWith('0001'));
  if (!breach) return null;
  return (
    <div className="banner banner-error" role="alert">
      <span>
        <strong>Economy frozen by the auditor.</strong> {summary.freezeReason ?? 'A ledger invariant failed.'}
        {summary.lastAudit.violations.length > 0 && ` ${summary.lastAudit.violations.join('; ')}`}
      </span>
      <AuditButtons />
    </div>
  );
}

function TabBar() {
  const { summary } = useEconomy();
  const badge = (id: TabId): number => (id === 'loans' ? (summary?.overdueLoans ?? 0) : id === 'trades' ? (summary?.inDoubtTrades ?? 0) : 0);
  return (
    <nav className="tabs" aria-label="Economy sections">
      {tabs.map((t) => {
        const n = badge(t.id);
        return (
          <NavLink key={t.id} to={t.id === 'overview' ? '/economy' : `/economy/${t.id}`} end>
            {t.title}
            {n > 0 && (
              <span className="badge badge-warn" aria-label={`${n} need attention`}>
                {n}!
              </span>
            )}
          </NavLink>
        );
      })}
    </nav>
  );
}

function Body({ tab }: { tab: TabId }) {
  const { noSession, error } = useEconomy();
  if (noSession && tab !== 'overview' && tab !== 'policy') {
    return <p className="notice">No session yet: the economy starts with the first player or the first start. Wallets and transactions appear then.</p>;
  }
  return (
    <>
      {error && <p role="alert">{error}</p>}
      {tab === 'overview' && <OverviewTab />}
      {tab === 'wallets' && <WalletsTab />}
      {tab === 'transactions' && <TransactionsTab />}
      {tab === 'loans' && <LoansTab />}
      {tab === 'trades' && <TradesTab />}
      {tab === 'policy' && <PolicyTab />}
    </>
  );
}

/** Route `/economy/:tab?`. One provider for all tabs, so live data and filters survive switching tabs. */
export function EconomyPage() {
  const { tab } = useParams();
  const current: TabId = tabs.find((t) => t.id === tab)?.id ?? 'overview';
  return (
    <EconomyProvider>
      <section className="economy">
        <Header />
        <Banners />
        <TabBar />
        <Body tab={current} />
        <Drawer />
      </section>
    </EconomyProvider>
  );
}
