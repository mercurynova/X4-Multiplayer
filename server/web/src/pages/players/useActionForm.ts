import { useState, type FormEvent } from 'react';
import { problemToFormErrors } from '../../lib/problem';

export interface ActionForm {
  reason: string;
  setReason: (r: string) => void;
  /** Per-field messages (camelCase request field names), from client checks and from the server's ApiProblem. */
  errors: Record<string, string>;
  /** Message for the form as a whole. */
  formError: string | null;
  busy: boolean;
  /** Form onSubmit: checks the reason, calls `run(reason)`, and on failure maps the ApiProblem to the fields. */
  submit: (e: FormEvent) => void;
}

/**
 * State shared by the kick / mute / ban dialogs. `check` returns client-side field errors for the dialog's own
 * fields (merged with the reason check). A required reason is blocked here before any request is sent.
 */
export function useActionForm(
  run: (reason: string) => Promise<void>,
  onDone: () => void,
  check?: () => Record<string, string>,
): ActionForm {
  const [reason, setReason] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const local = { ...check?.() };
    if (reason.trim().length === 0) local['reason'] = 'A reason is required.';
    setFormError(null);
    setErrors(local);
    if (Object.keys(local).length > 0) return;
    setBusy(true);
    run(reason.trim())
      .then(onDone)
      .catch((err: unknown) => {
        const fe = problemToFormErrors(err);
        setErrors(fe.fields);
        // A 400 with fields shows them inline; anything else (409 PlayerNotOnline, network) is the form message.
        setFormError(Object.keys(fe.fields).length > 0 ? null : fe.form);
      })
      .finally(() => setBusy(false));
  };

  return { reason, setReason, errors, formError, busy, submit };
}
