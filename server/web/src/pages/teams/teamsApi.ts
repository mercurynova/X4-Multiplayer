import { api } from '../../api/http';
import type {
  ApplyPresetRequest,
  AssignMemberRequest,
  CreateTeamRequest,
  PatchTeamPolicyRequest,
  PatchTeamRequest,
  SetRelationsRequest,
  TeamDto,
  TeamMemberDto,
  TeamPolicyDto,
  TeamPresetPreviewDto,
  TeamRelationsDto,
  TeamsStateDto,
} from '../../generated/generated';

const base = '/api/v1/teams';

export type PresetName = 'CoOp' | 'AlliedSeparate' | 'FreeForAll' | 'TwoTeams';

export const PRESETS: readonly { name: PresetName; label: string; hint: string }[] = [
  { name: 'CoOp', label: 'Everyone co-op', hint: 'One team, one faction' },
  { name: 'AlliedSeparate', label: 'All separate but allied', hint: 'A team per player, all allied' },
  { name: 'FreeForAll', label: 'Free-for-all', hint: 'A team per player, all hostile' },
  { name: 'TwoTeams', label: 'Two teams (versus)', hint: 'Two hostile teams, balanced' },
];

/** REST calls of the Teams & Factions page (server-design 4.4). Changes arrive back through the hub's teams topic. */
export const teamsApi = {
  state: () => api.get<TeamsStateDto>(base),
  create: (req: CreateTeamRequest) => api.post<TeamDto>(base, req),
  update: (id: number, req: PatchTeamRequest) => api.patch<TeamDto>(`${base}/${id}`, req),
  remove: (id: number, moveMembersTo?: number | null) =>
    api.delete(`${base}/${id}${moveMembersTo ? `?moveMembersTo=${moveMembersTo}` : ''}`),
  /** Moves a player to a team, or to Unassigned when `teamId` is null. */
  move: (playerId: number, teamId: number | null, role?: 'Member' | 'Leader') =>
    api.put<TeamMemberDto>(`${base}/members/${playerId}`, { teamId, role: role ?? null } satisfies AssignMemberRequest),
  setRelations: (entries: { teamA: number; teamB: number; relation: string }[]) =>
    api.put<TeamRelationsDto>(`${base}/relations`, { entries } satisfies SetRelationsRequest),
  previewPreset: (preset: PresetName) => api.get<TeamPresetPreviewDto>(`${base}/preset/${preset}/preview`),
  applyPreset: (preset: PresetName, confirm: boolean) =>
    api.post<TeamsStateDto>(`${base}/preset`, { preset, confirm } satisfies ApplyPresetRequest),
  patchPolicy: (req: Partial<PatchTeamPolicyRequest>) =>
    api.patch<TeamPolicyDto>(`${base}/policy`, {
      joinMode: null,
      autoAssign: null,
      allowCreateInLobby: null,
      lobbyTimeoutSeconds: null,
      maxTeams: null,
      defaultRelation: null,
      assetPolicy: null,
      allowFriendlyFire: null,
      allowAssetTransfer: null,
      moveAssetsWithPlayer: null,
      allowSelfTeamChange: null,
      relationChangePolicy: null,
      ...req,
    } satisfies PatchTeamPolicyRequest),
};
