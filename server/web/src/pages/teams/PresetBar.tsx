import { useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { ApiError } from '../../api/http';
import type { TeamPresetPreviewDto } from '../../generated/generated';
import { ConfirmDialog } from '../sessions/ConfirmDialog';
import { PRESETS, teamsApi, type PresetName } from './teamsApi';

/** Preset buttons. A click fetches the preview and asks for a confirmation that says what will change; nothing changes before. */
export function PresetBar({ canEdit }: { canEdit: boolean }) {
  const { toast } = useAlerts();
  const [open, setOpen] = useState<{ name: PresetName; label: string; preview: TeamPresetPreviewDto } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const choose = (name: PresetName, label: string) => {
    setError(null);
    setBusy(true);
    teamsApi
      .previewPreset(name)
      .then((preview) => setOpen({ name, label, preview }))
      .catch((e: unknown) => toast('error', e instanceof ApiError ? e.message : 'Could not preview the preset.'))
      .finally(() => setBusy(false));
  };

  const apply = () => {
    if (!open) return;
    setBusy(true);
    teamsApi
      .applyPreset(open.name, true)
      .then(() => {
        toast('success', `Applied "${open.label}".`);
        setOpen(null);
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not apply the preset.'))
      .finally(() => setBusy(false));
  };

  const p = open?.preview;
  return (
    <div className="preset-bar" role="group" aria-label="Presets">
      <span className="muted">Preset:</span>
      {PRESETS.map((x) => (
        <button key={x.name} type="button" className="ghost" title={x.hint} disabled={!canEdit || busy} onClick={() => choose(x.name, x.label)}>
          {x.label}
        </button>
      ))}
      {open && p && (
        <ConfirmDialog
          title={`Apply preset: ${open.label}`}
          confirmLabel={p.requiresConfirm ? 'Apply and move players' : 'Apply preset'}
          danger={p.requiresConfirm}
          busy={busy || p.blocked !== null}
          onConfirm={apply}
          onCancel={() => setOpen(null)}
        >
          <ul className="preview">
            <li>
              {p.teams.length} team{p.teams.length === 1 ? '' : 's'}: {p.teams.map((t) => `${t.name} (${t.memberCount})`).join(', ') || 'none'}
            </li>
            <li>
              {p.playersMoved} player{p.playersMoved === 1 ? '' : 's'} change team; {p.teamsRemoved} existing team{p.teamsRemoved === 1 ? ' is' : 's are'} replaced
            </li>
            <li>
              New players are placed by {p.autoAssign}
              {p.relation ? `; every pair of teams becomes ${p.relation}` : ''}
            </li>
            {p.running && <li>The session is running: the game changes while players are in it.</li>}
          </ul>
          {p.blocked && (
            <p role="alert">
              Not possible now: {p.blockedDetail ?? p.blocked}
            </p>
          )}
          {error && <p role="alert">{error}</p>}
        </ConfirmDialog>
      )}
    </div>
  );
}
