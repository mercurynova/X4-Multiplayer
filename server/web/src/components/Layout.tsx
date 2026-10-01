import { NavLink, Outlet } from 'react-router';
import { useAuth } from '../auth/AuthContext';
import { screens } from '../screens';

export function Layout() {
  const { logout } = useAuth();
  return (
    <div className="shell">
      <header className="topbar">
        <strong>X4MP</strong>
        <span className="spacer" />
        <button type="button" onClick={logout}>
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
