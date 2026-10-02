import { useState, type FormEvent, type ReactNode } from 'react';
import { ApiError } from '../../api/http';
import { problemToFormErrors } from '../../lib/problem';
import { Dialog } from '../players/Dialog';
import { describeEconomyError } from './economyApi';

export interface ReasonDialogProps {
  title: string;
  intro?: ReactNode;
  confirm: string;
  danger?: boolean;
  /** Called with the trimmed reason after the client checks passed. */
  run: (reason: string) => Promise<unknown>;
  onClose: () => void;
  /** Called after the server accepted the action (the dialog closes itself). */
  onDone: (message: string) => void;
  doneMessage: string;
  /** Client-side checks of the dialog's own fields; return field-name -> message. */
  check?: () => Record<string, string>;
  /** Sees a failed call (e.g. to reveal a "force" checkbox after WouldOverdraw). */
  onError?: (e: unknown) => void;
  /** The dialog's own fields, shown above the reason. */
  children?: ReactNode;
  /** Extra per-field errors from outside (rarely needed). */
  fieldErrors?: Record<string, string>;
}

/**
 * Every economy mutation needs a reason (it goes to the audit log). Shows the server's refusal in plain language
 * (WouldOverdraw, AlreadyReversed, NotReversible, ...) and keeps the dialog open so the admin can adjust and retry.
 */
export function ReasonDialog({
  title, intro, confirm, danger = true, run, onClose, onDone, doneMessage, check, onError, children, fieldErrors,
}: ReasonDialogProps) {
  const [reason, setReason] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const local = { ...check?.() };
    if (reason.trim() === '') local['reason'] = 'A reason is required.';
    setFormError(null);
    setErrors(local);
    if (Object.keys(local).length > 0) return;
    setBusy(true);
    run(reason.trim())
      .then(() => {
        onDone(doneMessage);
        onClose();
      })
      .catch((err: unknown) => {
        onError?.(err);
        const fe = problemToFormErrors(err);
        setErrors(fe.fields);
        setFormError(
          err instanceof ApiError && Object.keys(fe.fields).length > 0 ? null : describeEconomyError(err),
        );
      })
      .finally(() => setBusy(false));
  };

  const all = { ...errors, ...fieldErrors };
  return (
    <Dialog title={title} onClose={onClose}>
      <form onSubmit={submit} noValidate>
        {intro && <div className="muted">{intro}</div>}
        {children}
        <div>
          <label htmlFor="econ-reason">Reason</label>
          <textarea
            id="econ-reason"
            rows={2}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            aria-invalid={all['reason'] ? true : undefined}
            autoFocus
          />
          {all['reason'] && <span className="field-error">{all['reason']}</span>}
        </div>
        {formError && <p role="alert">{formError}</p>}
        <div className="dialog-buttons">
          <button type="button" className="ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className={danger ? 'danger' : undefined} disabled={busy}>
            {confirm}
          </button>
        </div>
      </form>
    </Dialog>
  );
}

/** A labelled field error under a dialog input. */
export function FieldError({ text }: { text: string | undefined }) {
  return text ? <span className="field-error">{text}</span> : null;
}
