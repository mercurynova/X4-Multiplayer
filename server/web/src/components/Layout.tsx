import { useState } from 'react';
import { Link, NavLink, Outlet } from 'react-router';
import { BannerSlot, ToastRegion } from '../alerts/AlertsProvider';
import { useAuth } from '../auth/AuthContext';
import { useHubState } from '../hub/HubProvider';
import type { ConnectionState } from '../hub/HubManager';
import { screens } from '../screens';
import { ThemeSwitch } from '../theme/ThemeProvider';
import { SessionStatus } from './SessionStatus';

const hubLabels: Record<ConnectionState, string> = {
  connected: 'live',
  connecting: 'connecting',
  reconnecting: 'reconnecting',
  disconnected: 'offline',
};

export function HubIndicator() {
  const state = useHubState();
  return (
    <span className={`hub-indicator hub-${state}`} role="status" aria-label={`Live updates: ${hubLabels[state]}`}>
      <span aria-hidden="true" className="dot" /> hub: {hubLabels[state]}
    </span>
  );
}

function UserMenu() {
  const { me, logout } = useAuth();
  return (
    <details className="user-menu">
      <summary>
        {me?.username} <span className="badge">{me?.role}</span>
      </summary>
      <div className="menu">
        <Link to="/account/password">Change password</Link>
        <button type="button" className="ghost" onClick={() => void logout()}>
          Sign out
        </button>
      </div>
    </details>
  );
}

export function Layout() {
  const { isAdmin } = useAuth();
  const [navOpen, setNavOpen] = useState(false);
  const visible = screens.filter((s) => !s.adminOnly || isAdmin);

  return (
    <div className="shell">
      <header className="topbar">
        <button
          type="button"
          className="ghost nav-toggle"
          aria-expanded={navOpen}
          aria-controls="main-nav"
          onClick={() => setNavOpen((o) => !o)}
        >
          Menu
        </button>
        <strong className="brand">X4MP</strong>
        <SessionStatus />
        <span className="spacer" />
        <ThemeSwitch />
        <UserMenu />
      </header>
      <nav id="main-nav" className={`sidenav${navOpen ? ' open' : ''}`} aria-label="Main" onClick={() => setNavOpen(false)}>
        <ul>
          {visible.map((s) => (
            <li key={s.path}>
              <NavLink to={s.path} end={s.path === '/'}>
                {s.title}
              </NavLink>
            </li>
          ))}
        </ul>
        <div className="nav-status">
          <HubIndicator />
        </div>
      </nav>
      <main className="content">
        <BannerSlot />
        <Outlet />
      </main>
      <ToastRegion />
    </div>
  );
}
