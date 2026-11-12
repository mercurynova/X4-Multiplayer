import { useState } from 'react';
import { AuditTab } from './AuditTab';
import { TokensTab } from './TokensTab';
import { UsersTab } from './UsersTab';
import '../players/players.css';
import './access.css';

type Tab = 'users' | 'tokens' | 'audit';

const TABS: { id: Tab; title: string }[] = [
  { id: 'users', title: 'Users' },
  { id: 'tokens', title: 'API tokens' },
  { id: 'audit', title: 'Audit log' },
];

/** Admin-only screen: accounts, API tokens and the audit trail (server-design 5.1, closing the M6 carry-over). */
export function AccessPage() {
  const [tab, setTab] = useState<Tab>('users');

  return (
    <section className="access">
      <header className="page-head">
        <h1>Access</h1>
      </header>

      <div className="tabs" role="tablist" aria-label="Access sections">
        {TABS.map((entry) => (
          <button
            key={entry.id}
            type="button"
            role="tab"
            id={`tab-${entry.id}`}
            aria-selected={tab === entry.id}
            aria-controls={`panel-${entry.id}`}
            className={tab === entry.id ? 'active' : ''}
            onClick={() => setTab(entry.id)}
          >
            {entry.title}
          </button>
        ))}
      </div>

      <div role="tabpanel" id={`panel-${tab}`} aria-labelledby={`tab-${tab}`}>
        {tab === 'users' && <UsersTab />}
        {tab === 'tokens' && <TokensTab />}
        {tab === 'audit' && <AuditTab />}
      </div>
    </section>
  );
}
