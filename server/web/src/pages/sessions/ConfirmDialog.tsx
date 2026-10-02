import type { ReactNode } from 'react';

/** Inline confirmation (no window.confirm: testable, and it can carry options such as the final-save checkbox). */
export function ConfirmDialog({
  title,
  children,
  confirmLabel,
  danger,
  busy,
  onConfirm,
  onCancel,
}: {
  title: string;
  children?: ReactNode;
  confirmLabel: string;
  danger?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <div className="confirm" role="alertdialog" aria-label={title} aria-modal="false">
      <strong>{title}</strong>
      {children}
      <div className="row">
        <button type="button" className={danger ? 'danger' : undefined} onClick={onConfirm} disabled={busy}>
          {confirmLabel}
        </button>
        <button type="button" className="ghost" onClick={onCancel} disabled={busy}>
          Cancel
        </button>
      </div>
    </div>
  );
}
