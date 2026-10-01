import { useState, type FormEvent } from 'react';
import { ApiError } from '../api/http';
import { useAuth } from '../auth/AuthContext';

const MIN_LENGTH = 12;

/** Password change form. `forced` is the first-login screen shown instead of the app until it succeeds. */
export function ChangePassword({ forced = false }: { forced?: boolean }) {
  const { changePassword, logout } = useAuth();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    if (next.length < MIN_LENGTH) return setError(`The new password must be at least ${MIN_LENGTH} characters.`);
    if (next !== confirm) return setError('The new passwords do not match.');
    setBusy(true);
    setError(null);
    changePassword(current, next)
      .catch((err: unknown) => setError(err instanceof ApiError ? err.message : 'Could not reach the server.'))
      .finally(() => setBusy(false));
  };

  return (
    <main className={forced ? 'centered' : undefined}>
      <form className="card" onSubmit={onSubmit}>
        <h1>Change password</h1>
        {forced && <p className="muted">You must choose a new password before continuing.</p>}
        <label>
          Current password
          <input
            type="password"
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>
        <label>
          New password
          <input
            type="password"
            value={next}
            onChange={(e) => setNext(e.target.value)}
            autoComplete="new-password"
            minLength={MIN_LENGTH}
            required
          />
        </label>
        <label>
          Confirm new password
          <input
            type="password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            autoComplete="new-password"
            required
          />
        </label>
        {error && <p role="alert">{error}</p>}
        <button type="submit" disabled={busy}>
          Change password
        </button>
        {forced && (
          <button type="button" onClick={() => void logout()}>
            Sign out
          </button>
        )}
      </form>
    </main>
  );
}
