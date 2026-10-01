import { Navigate, Outlet, useLocation } from 'react-router';
import { useAuth } from '../auth/AuthContext';
import { ChangePassword } from '../pages/ChangePassword';

export function RequireAuth() {
  const { status, mustChangePassword } = useAuth();
  const location = useLocation();
  if (status === 'loading') return <main className="centered"><p className="muted">Loading…</p></main>;
  if (status === 'anonymous') return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  if (mustChangePassword) return <ChangePassword forced />; // nothing else is reachable until it is changed
  return <Outlet />;
}
