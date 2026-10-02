import { useAlerts } from '../../alerts/AlertsProvider';
import { ApiError } from '../../api/http';
import type { ModPolicyDto, PatchModPolicyRequest } from '../../generated/generated';
import { formatTime } from './common';
import { modsApi } from './modsApi';

const VISIBILITY: Record<string, string> = {
  AdminsOnly: 'admins and mod editors only',
  AdminsAndViewers: 'admins, mod editors and viewers',
  AllPlayers: 'everyone, players included',
};

/** Source mode, unknown-mod default and enforcement. Each change is saved at once; the hub pushes the new policy back. */
export function PolicyCard({
  policy,
  canEdit,
  onSaved,
}: {
  policy: ModPolicyDto;
  canEdit: boolean;
  onSaved: (p: ModPolicyDto) => void;
}) {
  const { toast } = useAlerts();
  const save = (patch: PatchModPolicyRequest) =>
    modsApi
      .patchPolicy(patch)
      .then(onSaved)
      .catch((e: unknown) => toast('error', e instanceof ApiError ? e.message : 'Could not save the setting.'));
  const patch = (p: Partial<PatchModPolicyRequest>) => void save({ sourceMode: null, unknownDefault: null, enforcement: null, ...p });

  return (
    <div className="panel" aria-label="Mod policy">
      <h2>
        Policy <span className="badge">v{policy.version}</span>
      </h2>
      <fieldset className="policy-bar" disabled={!canEdit}>
        <legend className="visually-hidden">Mod policy settings</legend>
        <div className="row">
          <label>
            Mod list{' '}
            <select value={policy.sourceMode} onChange={(e) => patch({ sourceMode: e.target.value })}>
              <option value="AuthorityDefines">Authority defines</option>
              <option value="AdminList">Admin list</option>
            </select>
          </label>
          <label>
            Unknown mods{' '}
            <select value={policy.unknownDefault} onChange={(e) => patch({ unknownDefault: e.target.value })}>
              <option value="Block">Block</option>
              <option value="AllowClientOnly">Allow client-only</option>
              <option value="AllowAll">Allow all</option>
            </select>
          </label>
          <label>
            Enforcement{' '}
            <select value={policy.enforcement} onChange={(e) => patch({ enforcement: e.target.value })}>
              <option value="Strict">Strict (refuse)</option>
              <option value="Warn">Warn (admit and flag)</option>
            </select>
          </label>
        </div>
      </fieldset>
      <p className="muted">
        The policy version goes up on every edit.
        {policy.updatedAt && ` Last changed ${formatTime(policy.updatedAt)}${policy.updatedBy ? ` by ${policy.updatedBy}` : ''}.`} Changes apply to the next
        join; nobody is kicked.
      </p>
      <p className="muted">
        {policy.sourceMode === 'AuthorityDefines'
          ? 'The authority’s enabled DLC and sim mods are required; other mods follow the unknown-mods setting.'
          : 'This list is authoritative and the authority is checked against it too.'}{' '}
        Who sees players&apos; mod lists: {VISIBILITY[policy.modListVisibility] ?? policy.modListVisibility}.
        {policy.authorityReportedAt
          ? ` Authority reported its mods ${formatTime(policy.authorityReportedAt)}.`
          : policy.authorityPlayerId === null
            ? ' The authority has not reported its mods.'
            : ''}
      </p>
      {!canEdit && <p className="muted">You can view the mod list but not change it.</p>}
    </div>
  );
}
