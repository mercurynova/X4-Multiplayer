import { useState, type FormEvent, type ReactNode } from 'react';
import type { AdminUserDto, ApiTokenCreatedDto } from '../../generated/generated';
import { problemToFormErrors, type FormErrors } from '../../lib/problem';
import { Dialog } from '../players/Dialog';
import { accessApi, ROLES } from './accessApi';

const NO_ERRORS: FormErrors = { fields: {}, all: {}, form: '' };

function Field({ label, error, children, hint }: { label: string; error?: string; children: ReactNode; hint?: string }) {
  return (
    <label>
      {label}
      {children}
      {hint && <span className="muted"> {hint}</span>}
      {error && (
        <span className="field-error" role="alert">
          {error}
        </span>
      )}
    </label>
  );
}

function Problems({ errors }: { errors: FormErrors }) {
  return errors.form ? (
    <p className="field-error" role="alert">
      {errors.form}
    </p>
  ) : null;
}

/** Creates an account; the server marks it must-change and audits the change (never the password). */
export function UserCreateDialog({ onClose, onSaved }: { onClose: () => void; onSaved: (message: string) => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<string>('Viewer');
  const [errors, setErrors] = useState<FormErrors>(NO_ERRORS);
  const [busy, setBusy] = useState(false);
  const f = errors.fields;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setErrors(NO_ERRORS);
    accessApi
      .createUser({ username: username.trim(), password, role })
      .then((user) => {
        onSaved(`Created ${user.username} (${user.role}).`);
        onClose();
      })
      .catch((err: unknown) => setErrors(problemToFormErrors(err)))
      .finally(() => setBusy(false));
  };

  return (
    <Dialog title="Add user" onClose={onClose}>
      <form onSubmit={submit} noValidate>
        <Field label="Username" error={f['username']}>
          <input value={username} onChange={(e) => setUsername(e.target.value)} required autoFocus aria-invalid={f['username'] ? true : undefined} />
        </Field>
        <Field label="Initial password" error={f['password']} hint="The account changes it at first login.">
          <input
            type="password"
            aria-label="Initial password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            aria-invalid={f['password'] ? true : undefined}
          />
        </Field>
        <Field label="Role" error={f['role']}>
          <select value={role} onChange={(e) => setRole(e.target.value)}>
            {ROLES.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </Field>
        <Problems errors={errors} />
        <div className="dialog-actions">
          <button type="button" className="ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" disabled={busy}>
            {busy ? 'Saving…' : 'Create user'}
          </button>
        </div>
      </form>
    </Dialog>
  );
}

/** Changes a role and/or resets the password. A password reset ends the account's sessions and revokes its tokens. */
export function UserEditDialog({ user, onClose, onSaved }: { user: AdminUserDto; onClose: () => void; onSaved: (message: string) => void }) {
  const [role, setRole] = useState(user.role);
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<FormErrors>(NO_ERRORS);
  const [busy, setBusy] = useState(false);
  const f = errors.fields;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setErrors(NO_ERRORS);
    accessApi
      .patchUser(user.id, { role: role === user.role ? null : role, password: password === '' ? null : password })
      .then((updated) => {
        onSaved(`Saved ${updated.username}.`);
        onClose();
      })
      .catch((err: unknown) => setErrors(problemToFormErrors(err)))
      .finally(() => setBusy(false));
  };

  return (
    <Dialog title={`Edit ${user.username}`} onClose={onClose}>
      <form onSubmit={submit} noValidate>
        <Field label="Role" error={f['role']}>
          <select value={role} onChange={(e) => setRole(e.target.value)}>
            {ROLES.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </Field>
        <Field label="New password" error={f['password']} hint="Leave empty to keep the current one. A reset signs the account out everywhere.">
          <input
            type="password"
            aria-label="New password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            aria-invalid={f['password'] ? true : undefined}
          />
        </Field>
        <Problems errors={errors} />
        <div className="dialog-actions">
          <button type="button" className="ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" disabled={busy}>
            {busy ? 'Saving…' : 'Save'}
          </button>
        </div>
      </form>
    </Dialog>
  );
}

/** Creates a token and shows its plaintext exactly once; the list never carries it again. */
export function TokenCreateDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const [name, setName] = useState('');
  const [role, setRole] = useState<string>('Viewer');
  const [created, setCreated] = useState<ApiTokenCreatedDto | null>(null);
  const [copied, setCopied] = useState(false);
  const [errors, setErrors] = useState<FormErrors>(NO_ERRORS);
  const [busy, setBusy] = useState(false);
  const f = errors.fields;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setErrors(NO_ERRORS);
    accessApi
      .createToken({ name: name.trim(), role })
      .then(setCreated)
      .catch((err: unknown) => setErrors(problemToFormErrors(err)))
      .finally(() => setBusy(false));
  };

  const copy = () => {
    if (!created) return;
    void navigator.clipboard?.writeText(created.token).then(() => setCopied(true)).catch(() => setCopied(false));
  };

  if (created) {
    return (
      <Dialog title="Token created" onClose={() => { onCreated(); onClose(); }}>
        <p>
          Copy <strong>{created.name}</strong> now: the token is not shown again.
        </p>
        <p>
          <code className="token-plain">{created.token}</code>
        </p>
        <div className="dialog-actions">
          <button type="button" className="ghost" onClick={copy}>
            {copied ? 'Copied' : 'Copy'}
          </button>
          <button type="button" onClick={() => { onCreated(); onClose(); }}>
            Done
          </button>
        </div>
      </Dialog>
    );
  }

  return (
    <Dialog title="Add API token" onClose={onClose}>
      <form onSubmit={submit} noValidate>
        <Field label="Name" error={f['name']}>
          <input value={name} onChange={(e) => setName(e.target.value)} required autoFocus aria-invalid={f['name'] ? true : undefined} />
        </Field>
        <Field label="Role" error={f['role']}>
          <select value={role} onChange={(e) => setRole(e.target.value)}>
            {ROLES.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </Field>
        <p className="muted">The role is the most the token can do. Revoking it takes effect immediately.</p>
        <Problems errors={errors} />
        <div className="dialog-actions">
          <button type="button" className="ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" disabled={busy}>
            {busy ? 'Creating…' : 'Create token'}
          </button>
        </div>
      </form>
    </Dialog>
  );
}
