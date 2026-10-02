import { useState, type FormEvent } from 'react';
import type { TeamDto } from '../../generated/generated';
import { problemToFormErrors } from '../../lib/problem';
import { ConfirmDialog } from '../sessions/ConfirmDialog';
import { Dialog } from '../players/Dialog';
import { teamsApi } from './teamsApi';

const PALETTE = ['#3FA7FF', '#FF5A5A', '#4CD964', '#FFC83D', '#B26BFF', '#2EE6D6', '#FF8A3D', '#E8E8E8'];

/** Create a team (no `team`) or edit one. The lobby password is write-only: blank keeps it, "Remove password" clears it. */
export function TeamFormDialog({
  team,
  nextSlot,
  onClose,
  onDone,
}: {
  team?: TeamDto;
  nextSlot: number;
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  const [name, setName] = useState(team?.name ?? '');
  const [color, setColor] = useState(team?.color ?? PALETTE[(nextSlot - 1) % PALETTE.length]!);
  const [locked, setLocked] = useState(team?.locked ?? false);
  const [max, setMax] = useState(team?.maxMembers ? String(team.maxMembers) : '');
  const [password, setPassword] = useState('');
  const [clearPassword, setClearPassword] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const local: Record<string, string> = {};
    if (name.trim() === '') local['name'] = 'A name is required.';
    const maxMembers = max.trim() === '' ? 0 : Number(max);
    if (!Number.isInteger(maxMembers) || maxMembers < 0 || maxMembers > 64) local['maxMembers'] = 'Use a whole number from 1 to 64, or leave it empty.';
    setErrors(local);
    setFormError(null);
    if (Object.keys(local).length > 0) return;
    setBusy(true);
    const pw = clearPassword ? '' : password === '' ? null : password;
    const run = team
      ? teamsApi.update(team.id, { name: name.trim(), color, factionSlot: null, leaderPlayerId: null, locked, maxMembers, password: pw })
      : teamsApi.create({ name: name.trim(), color, factionSlot: null, maxMembers: maxMembers || null, locked, password: pw });
    run
      .then(() => {
        onDone(team ? `Saved ${name.trim()}.` : `Created ${name.trim()}.`);
        onClose();
      })
      .catch((err: unknown) => {
        const fe = problemToFormErrors(err);
        setErrors(fe.fields);
        setFormError(Object.keys(fe.fields).length > 0 ? null : fe.form);
      })
      .finally(() => setBusy(false));
  };

  return (
    <Dialog title={team ? `Edit ${team.name}` : 'New team'} onClose={onClose}>
      <form onSubmit={submit} noValidate>
        <div>
          <label htmlFor="team-name">Name</label>
          <input
            id="team-name"
            value={name}
            maxLength={24}
            onChange={(e) => setName(e.target.value)}
            aria-invalid={errors['name'] ? true : undefined}
            autoFocus
          />
          {errors['name'] && <span className="field-error">{errors['name']}</span>}
        </div>
        <div>
          <label htmlFor="team-color">Colour</label>
          <input id="team-color" type="color" value={color} onChange={(e) => setColor(e.target.value.toUpperCase())} />
          {errors['color'] && <span className="field-error">{errors['color']}</span>}
        </div>
        <div>
          <label htmlFor="team-max">Member limit</label>
          <input id="team-max" inputMode="numeric" placeholder="No limit" value={max} onChange={(e) => setMax(e.target.value)} aria-invalid={errors['maxMembers'] ? true : undefined} />
          {errors['maxMembers'] && <span className="field-error">{errors['maxMembers']}</span>}
        </div>
        <label className="check">
          <input type="checkbox" checked={locked} onChange={(e) => setLocked(e.target.checked)} />
          Locked (players cannot choose this team in the lobby)
        </label>
        <div>
          <label htmlFor="team-password">Lobby password</label>
          <input
            id="team-password"
            type="password"
            autoComplete="new-password"
            placeholder={team?.hasPassword ? '(unchanged)' : 'None'}
            value={password}
            disabled={clearPassword}
            onChange={(e) => setPassword(e.target.value)}
          />
        </div>
        {team?.hasPassword && (
          <label className="check">
            <input type="checkbox" checked={clearPassword} onChange={(e) => setClearPassword(e.target.checked)} />
            Remove password
          </label>
        )}
        {formError && <p role="alert">{formError}</p>}
        <div className="dialog-buttons">
          <button type="button" className="ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" disabled={busy}>
            {team ? 'Save' : 'Create team'}
          </button>
        </div>
      </form>
    </Dialog>
  );
}

/** Delete a team; its members go to another team or to Unassigned. */
export function DeleteTeamDialog({
  team,
  others,
  onClose,
  onDone,
}: {
  team: TeamDto;
  others: readonly TeamDto[];
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  const [target, setTarget] = useState('none');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <Dialog title={`Delete ${team.name}`} onClose={onClose}>
      <ConfirmDialog
        title={`Delete the team ${team.name}?`}
        confirmLabel="Delete team"
        danger
        busy={busy}
        onCancel={onClose}
        onConfirm={() => {
          setBusy(true);
          teamsApi
            .remove(team.id, target === 'none' ? null : Number(target))
            .then(() => {
              onDone(`Deleted ${team.name}.`);
              onClose();
            })
            .catch((e: unknown) => setError(problemToFormErrors(e).form))
            .finally(() => setBusy(false));
        }}
      >
        {team.memberCount > 0 ? (
          <p>
            <label htmlFor="delete-target">Move its {team.memberCount} member(s) to</label>
            <select id="delete-target" value={target} onChange={(e) => setTarget(e.target.value)}>
              <option value="none">Unassigned</option>
              {others.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          </p>
        ) : (
          <p className="muted">The team has no members.</p>
        )}
        {error && <p role="alert">{error}</p>}
      </ConfirmDialog>
    </Dialog>
  );
}
