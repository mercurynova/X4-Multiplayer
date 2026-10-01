import { NavLink, Outlet } from 'react-router';
import { useAuth } from '../auth/AuthContext';
import { screens } from '../screens';

export function Layout() {
  const { logout, me } = useAuth();
  return (
    <div className="shell">
      <header className="topbar">
        <strong>X4MP</strong>
        <span className="spacer" />
        <span className="muted">{me?.username}</span>
        <button type="button" onClick={() => void logout()}>
          Sign out
        </button>
      </header>
      <nav className="sidenav" aria-label="Main">
        <ul>
          {screens.map((s) => (
            <li key={s.path}>
              <NavLink to={s.path} end={s.path === '/'}>
                {s.title}
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>
      <main className="content">
        <Outlet />
      </main>
    </div>
  );
}
