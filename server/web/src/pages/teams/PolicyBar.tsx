import { useId } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { ApiError } from '../../api/http';
import type { PatchTeamPolicyRequest, TeamPolicyDto } from '../../generated/generated';
import { teamsApi } from './teamsApi';

const JOIN_MODES: readonly { value: string; label: string }[] = [
  { value: 'Auto', label: 'Auto' },
  { value: 'Lobby', label: 'Lobby' },
  { value: 'AdminAssign', label: 'Admin assigns' },
];

/** Join mode and the team rules a joining player is placed by. Each change is saved at once; the hub pushes the result back. */
export function PolicyBar({ policy, canEdit, onChanged }: { policy: TeamPolicyDto; canEdit: boolean; onChanged: (p: TeamPolicyDto) => void }) {
  const { toast } = useAlerts();
  const name = useId();
  const save = (patch: Partial<PatchTeamPolicyRequest>) =>
    teamsApi
      .patchPolicy(patch)
      .then(onChanged)
      .catch((e: unknown) => toast('error', e instanceof ApiError ? e.message : 'Could not save the setting.'));

  return (
    <fieldset className="policy-bar" disabled={!canEdit}>
      <legend className="visually-hidden">Team settings</legend>
      <div role="radiogroup" aria-label="Join mode" className="row">
        <span className="muted">Join mode</span>
        {JOIN_MODES.map((m) => (
          <label key={m.value} className="check">
            <input type="radio" name={name} value={m.value} checked={policy.joinMode === m.value} onChange={() => void save({ joinMode: m.value })} />
            {m.label}
          </label>
        ))}
      </div>
      <div className="row">
        <label>
          Auto-assign{' '}
          <select
            value={policy.autoAssign}
            disabled={!canEdit || policy.joinMode !== 'Auto'}
            onChange={(e) => void save({ autoAssign: e.target.value })}
          >
            <option value="SingleTeam">Everyone in one team</option>
            <option value="Balance">Balance the teams</option>
            <option value="NewTeamPerPlayer">A new team per player</option>
          </select>
        </label>
        <label className="check">
          <input
            type="checkbox"
            checked={policy.allowCreateInLobby}
            disabled={!canEdit || policy.joinMode !== 'Lobby'}
            onChange={(e) => void save({ allowCreateInLobby: e.target.checked })}
          />
          Allow creating a team in the lobby
        </label>
        <label>
          Asset command{' '}
          <select value={policy.assetPolicy} onChange={(e) => void save({ assetPolicy: e.target.value })}>
            <option value="SharedCommand">Shared by the team</option>
            <option value="OwnerOnly">Owner only</option>
            <option value="OwnerAndLeader">Owner and leader</option>
          </select>
        </label>
      </div>
    </fieldset>
  );
}
