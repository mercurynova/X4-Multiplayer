import { useCallback, useEffect, useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import type { AdminUserDto } from '../../generated/generated';
import { Dialog } from '../players/Dialog';
import { accessApi, formatTime } from './accessApi';
import { UserCreateDialog, UserEditDialog } from './AccessDialogs';

/** Accounts of the admin API: create, change role/reset password, delete. Every change lands in the audit log. */
export function UsersTab() {
  const { toast } = useAlerts();
  const [users, setUsers] = useState<AdminUserDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<AdminUserDto | null>(null);
  const [deleting, setDeleting] = useState<AdminUserDto | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    accessApi
      .users()
      .then((list) => {
        setUsers(list);
        setError(null);
      })
      .catch((err: unknown) => setError(err instanceof Error ? err.message : 'Could not load users.'));
  }, []);

  useEffect(load, [load]);

  const done = (message: string) => {
    toast('success', message);
    load();
  };

  const remove = () => {
    if (!deleting) return;
    setBusy(true);
    accessApi
      .deleteUser(deleting.id)
      .then(() => {
        toast('success', `Deleted ${deleting.username}.`);
        setDeleting(null);
        load();
      })
      .catch((err: unknown) => toast('error', err instanceof Error ? err.message : 'Could not delete the user.'))
      .finally(() => setBusy(false));
  };

  return (
    <>
      <div className="toolbar">
        <button type="button" onClick={() => setCreating(true)}>
          + Add user
        </button>
        <button type="button" className="ghost" onClick={load}>
          Refresh
        </button>
      </div>

      {error && <p role="alert">{error}</p>}
      {users === null && !error && <p className="muted">Loading users…</p>}
      {users !== null && users.length === 0 && <p className="muted">No accounts.</p>}

      {users !== null && users.length > 0 && (
        <div className="table-wrap">
          <table className="data">
            <caption className="visually-hidden">Admin accounts</caption>
            <thead>
              <tr>
                <th scope="col">Username</th>
                <th scope="col">Role</th>
                <th scope="col">Must change</th>
                <th scope="col">Created</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => (
                <tr key={user.id}>
                  <td>{user.username}</td>
                  <td>
                    <span className="badge">{user.role}</span>
                  </td>
                  <td>{user.mustChange ? 'yes' : ''}</td>
                  <td>{formatTime(user.createdAt)}</td>
                  <td className="row-actions">
                    <button type="button" className="ghost" onClick={() => setEditing(user)}>
                      Edit
                    </button>
                    <button type="button" className="ghost" onClick={() => setDeleting(user)}>
                      Delete
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {creating && <UserCreateDialog onClose={() => setCreating(false)} onSaved={done} />}
      {editing && <UserEditDialog user={editing} onClose={() => setEditing(null)} onSaved={done} />}

      {deleting && (
        <Dialog title={`Delete ${deleting.username}?`} onClose={() => setDeleting(null)}>
          <p>The account is removed and its API tokens are revoked. This cannot be undone; the audit log keeps the change.</p>
          <div className="dialog-actions">
            <button type="button" className="ghost" onClick={() => setDeleting(null)}>
              Cancel
            </button>
            <button type="button" className="danger" disabled={busy} onClick={remove}>
              {busy ? 'Deleting…' : 'Delete user'}
            </button>
          </div>
        </Dialog>
      )}
    </>
  );
}
