import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router';
import { ApiError } from '../api/http';
import { useAuth } from '../auth/AuthContext';

function messageFor(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 401) return 'Invalid username or password.';
    if (e.status === 429) return 'Too many attempts. Wait a minute and try again.';
    if (e.status === 403) return 'Access from this network is not allowed.';
    return e.message;
  }
  return 'Could not reach the server.';
}

export function Login() {
  const { isAuthenticated, login, notice } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const from = (location.state as { from?: string } | null)?.from ?? '/';

  if (isAuthenticated) return <Navigate to={from} replace />;

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    login(username, password)
      .then(() => navigate(from, { replace: true }))
      .catch((err: unknown) => setError(messageFor(err)))
      .finally(() => setBusy(false));
  };

  return (
    <main className="centered">
      <form className="card" onSubmit={onSubmit}>
        <h1>Sign in</h1>
        {notice && <p role="status" className="notice">{notice}</p>}
        <label>
          Username
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" required />
        </label>
        <label>
          Password
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>
        {error && <p role="alert">{error}</p>}
        <button type="submit" disabled={busy}>
          Sign in
        </button>
      </form>
    </main>
  );
}
