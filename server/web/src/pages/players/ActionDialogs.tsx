import { useState, type ReactNode } from 'react';
import type { PlayerDto } from '../../generated/generated';
import { Dialog } from './Dialog';
import { playersApi } from './playersApi';
import { useActionForm, type ActionForm } from './useActionForm';

function ReasonField({ form }: { form: ActionForm }) {
  return (
    <div>
      <label htmlFor="action-reason">Reason</label>
      <textarea
        id="action-reason"
        rows={2}
        value={form.reason}
        onChange={(e) => form.setReason(e.target.value)}
        aria-invalid={form.errors['reason'] ? true : undefined}
        aria-describedby={form.errors['reason'] ? 'reason-error' : undefined}
        autoFocus
      />
      {form.errors['reason'] && (
        <span id="reason-error" className="field-error">
          {form.errors['reason']}
        </span>
      )}
    </div>
  );
}

function Buttons({ form, confirm, onClose }: { form: ActionForm; confirm: string; onClose: () => void }) {
  return (
    <>
      {form.formError && <p role="alert">{form.formError}</p>}
      <div className="dialog-buttons">
        <button type="button" className="ghost" onClick={onClose}>
          Cancel
        </button>
        <button type="submit" className="danger" disabled={form.busy}>
          {confirm}
        </button>
      </div>
    </>
  );
}

export interface DialogProps {
  onClose: () => void;
  /** Called after the server accepted the action. */
  onDone: (message: string) => void;
}

export function KickDialog({ player, onClose, onDone }: DialogProps & { player: PlayerDto }) {
  const form = useActionForm(
    (reason) => playersApi.kick(player.id, reason),
    () => {
      onDone(`Kicked ${player.name}.`);
      onClose();
    },
  );
  return (
    <Dialog title={`Kick ${player.name}`} onClose={onClose}>
      <form onSubmit={form.submit} noValidate>
        <p className="muted">The player is disconnected now and may rejoin. The reason is shown to the player and logged.</p>
        <ReasonField form={form} />
        <Buttons form={form} confirm="Kick" onClose={onClose} />
      </form>
    </Dialog>
  );
}

const muteChoices: readonly { label: string; minutes: number | null }[] = [
  { label: '10 minutes', minutes: 10 },
  { label: '1 hour', minutes: 60 },
  { label: 'Until lifted', minutes: null },
];

export function MuteDialog({ player, onClose, onDone }: DialogProps & { player: PlayerDto }) {
  const [choice, setChoice] = useState(0);
  const form = useActionForm(
    (reason) => playersApi.mute(player.id, muteChoices[choice]?.minutes ?? null, reason),
    () => {
      onDone(`Muted ${player.name}.`);
      onClose();
    },
  );
  return (
    <Dialog title={`Mute ${player.name}`} onClose={onClose}>
      <form onSubmit={form.submit} noValidate>
        <label>
          Duration
          <select value={choice} onChange={(e) => setChoice(Number(e.target.value))}>
            {muteChoices.map((c, i) => (
              <option key={c.label} value={i}>
                {c.label}
              </option>
            ))}
          </select>
          {form.errors['minutes'] && <span className="field-error">{form.errors['minutes']}</span>}
        </label>
        <ReasonField form={form} />
        <Buttons form={form} confirm="Mute" onClose={onClose} />
      </form>
    </Dialog>
  );
}

const durations: readonly { value: string; label: string; minutes: number | null }[] = [
  { value: '1h', label: '1 hour', minutes: 60 },
  { value: '1d', label: '1 day', minutes: 1440 },
  { value: '7d', label: '7 days', minutes: 10_080 },
  { value: 'perm', label: 'Permanent', minutes: null },
  { value: 'custom', label: 'Custom (minutes)', minutes: null },
];

type BanKind = 'player' | 'keyHash' | 'ip';

/**
 * Ban dialog. With a `player` it bans that player (optionally also their last address as /32); without one it bans
 * a key hash or an address / CIDR block.
 */
export function BanDialog({ player, onClose, onDone }: DialogProps & { player?: PlayerDto }) {
  const [kind, setKind] = useState<BanKind>(player ? 'player' : 'ip');
  const [keyHash, setKeyHash] = useState('');
  const [cidr, setCidr] = useState('');
  const [alsoIp, setAlsoIp] = useState(false);
  const [duration, setDuration] = useState('perm');
  const [custom, setCustom] = useState('');

  const customMinutes = custom.trim() === '' ? NaN : Number(custom);
  const minutes = duration === 'custom' ? customMinutes : (durations.find((d) => d.value === duration)?.minutes ?? null);
  const targetText = kind === 'player' ? (player?.name ?? '') : kind === 'keyHash' ? keyHash.trim() : cidr.trim();

  const form = useActionForm(
    (reason) =>
      playersApi
        .ban({
          playerId: kind === 'player' ? (player?.id ?? null) : null,
          keyHash: kind === 'keyHash' ? keyHash.trim() : null,
          ipCidr: kind === 'ip' ? cidr.trim() : kind === 'player' && alsoIp && player?.lastIp ? `${player.lastIp}/32` : null,
          reason,
          durationMinutes: minutes,
        })
        .then(() => undefined),
    () => {
      onDone(`Banned ${targetText}.`);
      onClose();
    },
    () => {
      const e: Record<string, string> = {};
      if (kind === 'keyHash' && keyHash.trim() === '') e['keyHash'] = 'Enter the 64-digit key hash.';
      if (kind === 'ip' && cidr.trim() === '') e['ipCidr'] = 'Enter an address or a CIDR block such as 10.1.0.0/16.';
      if (duration === 'custom' && !(Number.isInteger(minutes) && (minutes ?? 0) >= 1)) {
        e['durationMinutes'] = 'Enter a whole number of minutes, 1 or more.';
      }
      return e;
    },
  );

  const kinds: { id: BanKind; label: string }[] = [
    ...(player ? [{ id: 'player' as const, label: `Player ${player.name}` }] : []),
    { id: 'keyHash', label: 'Key hash' },
    { id: 'ip', label: 'IP address / CIDR' },
  ];

  let targetField: ReactNode = null;
  if (kind === 'keyHash') {
    targetField = (
      <label>
        Key hash (SHA-256, hex)
        <input
          value={keyHash}
          onChange={(e) => setKeyHash(e.target.value)}
          aria-invalid={form.errors['keyHash'] ? true : undefined}
          spellCheck={false}
        />
        {form.errors['keyHash'] && <span className="field-error">{form.errors['keyHash']}</span>}
      </label>
    );
  } else if (kind === 'ip') {
    targetField = (
      <label>
        Address or CIDR block
        <input
          value={cidr}
          onChange={(e) => setCidr(e.target.value)}
          placeholder="10.1.0.0/16"
          aria-invalid={form.errors['ipCidr'] ? true : undefined}
          spellCheck={false}
        />
        {form.errors['ipCidr'] && <span className="field-error">{form.errors['ipCidr']}</span>}
      </label>
    );
  } else if (player?.lastIp) {
    targetField = (
      <label className="check">
        <input type="checkbox" checked={alsoIp} onChange={(e) => setAlsoIp(e.target.checked)} />
        Also ban the address {player.lastIp} (/32)
      </label>
    );
  }

  return (
    <Dialog title={player ? `Ban ${player.name}` : 'Ban an address or key hash'} onClose={onClose}>
      <form onSubmit={form.submit} noValidate>
        {kinds.length > 1 && (
          <fieldset>
            <legend>Ban</legend>
            {kinds.map((k) => (
              <label key={k.id} className="check">
                <input type="radio" name="ban-kind" checked={kind === k.id} onChange={() => setKind(k.id)} />
                {k.label}
              </label>
            ))}
          </fieldset>
        )}
        {form.errors['target'] && <p className="field-error">{form.errors['target']}</p>}
        {targetField}
        <label>
          Duration
          <select value={duration} onChange={(e) => setDuration(e.target.value)}>
            {durations.map((d) => (
              <option key={d.value} value={d.value}>
                {d.label}
              </option>
            ))}
          </select>
        </label>
        {duration === 'custom' && (
          <label>
            Minutes
            <input
              inputMode="numeric"
              value={custom}
              onChange={(e) => setCustom(e.target.value)}
              aria-invalid={form.errors['durationMinutes'] ? true : undefined}
            />
          </label>
        )}
        {form.errors['durationMinutes'] && <span className="field-error">{form.errors['durationMinutes']}</span>}
        <ReasonField form={form} />
        <Buttons form={form} confirm="Ban" onClose={onClose} />
      </form>
    </Dialog>
  );
}

/** Plain confirmation (no reason in the API): unban, release name, unmute. */
export function ConfirmDialog({
  title,
  body,
  confirm,
  run,
  onClose,
  onDone,
  doneMessage,
}: DialogProps & { title: string; body: string; confirm: string; run: () => Promise<unknown>; doneMessage: string }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <Dialog title={title} onClose={onClose}>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          setBusy(true);
          setError(null);
          run()
            .then(() => {
              onDone(doneMessage);
              onClose();
            })
            .catch((err: unknown) => setError(err instanceof Error ? err.message : 'Request failed.'))
            .finally(() => setBusy(false));
        }}
      >
        <p>{body}</p>
        {error && <p role="alert">{error}</p>}
        <div className="dialog-buttons">
          <button type="button" className="ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" disabled={busy} autoFocus>
            {confirm}
          </button>
        </div>
      </form>
    </Dialog>
  );
}
