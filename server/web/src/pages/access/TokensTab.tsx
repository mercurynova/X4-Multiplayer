import { useCallback, useEffect, useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import type { ApiTokenDto } from '../../generated/generated';
import { Dialog } from '../players/Dialog';
import { accessApi, formatTime } from './accessApi';
import { TokenCreateDialog } from './AccessDialogs';

/** API tokens: created with a role, shown once, listed without the secret, revoked immediately. */
export function TokensTab() {
  const { toast } = useAlerts();
  const [tokens, setTokens] = useState<ApiTokenDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [revoking, setRevoking] = useState<ApiTokenDto | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    accessApi
      .tokens()
      .then((list) => {
        setTokens(list);
        setError(null);
      })
      .catch((err: unknown) => setError(err instanceof Error ? err.message : 'Could not load tokens.'));
  }, []);

  useEffect(load, [load]);

  const revoke = () => {
    if (!revoking) return;
    setBusy(true);
    accessApi
      .revokeToken(revoking.id)
      .then(() => {
        toast('success', `Revoked ${revoking.name}.`);
        setRevoking(null);
        load();
      })
      .catch((err: unknown) => toast('error', err instanceof Error ? err.message : 'Could not revoke the token.'))
      .finally(() => setBusy(false));
  };

  return (
    <>
      <div className="toolbar">
        <button type="button" onClick={() => setCreating(true)}>
          + Add token
        </button>
        <button type="button" className="ghost" onClick={load}>
          Refresh
        </button>
      </div>

      {error && <p role="alert">{error}</p>}
      {tokens === null && !error && <p className="muted">Loading tokens…</p>}
      {tokens !== null && tokens.length === 0 && <p className="muted">No API tokens.</p>}

      {tokens !== null && tokens.length > 0 && (
        <div className="table-wrap">
          <table className="data">
            <caption className="visually-hidden">API tokens</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Role</th>
                <th scope="col">Owner</th>
                <th scope="col">Created</th>
                <th scope="col">Last used</th>
                <th scope="col">Status</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {tokens.map((token) => (
                <tr key={token.id}>
                  <td>{token.name}</td>
                  <td>
                    <span className="badge">{token.role}</span>
                  </td>
                  <td>{token.owner ?? <span className="muted">server</span>}</td>
                  <td>{formatTime(token.createdAt)}</td>
                  <td>{formatTime(token.lastUsedAt) || <span className="muted">never</span>}</td>
                  <td>
                    <span className={`badge ${token.revoked ? 'badge-bad' : 'badge-ok'}`}>{token.revoked ? 'Revoked' : 'Active'}</span>
                  </td>
                  <td className="row-actions">
                    {!token.revoked && (
                      <button type="button" className="ghost" onClick={() => setRevoking(token)}>
                        Revoke
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {creating && <TokenCreateDialog onClose={() => setCreating(false)} onCreated={load} />}

      {revoking && (
        <Dialog title={`Revoke ${revoking.name}?`} onClose={() => setRevoking(null)}>
          <p>Anything using this token is refused from the next request. The token cannot be restored.</p>
          <div className="dialog-actions">
            <button type="button" className="ghost" onClick={() => setRevoking(null)}>
              Cancel
            </button>
            <button type="button" className="danger" disabled={busy} onClick={revoke}>
              {busy ? 'Revoking…' : 'Revoke token'}
            </button>
          </div>
        </Dialog>
      )}
    </>
  );
}
