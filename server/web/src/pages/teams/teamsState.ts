import type {
  TeamDto,
  TeamMemberDto,
  TeamPolicyDto,
  TeamRelationsDto,
  TeamsStateDto,
} from '../../generated/generated';
import { E, SNAPSHOT_EVENT } from '../../hub/contract';

export type Relation = 'Allied' | 'Neutral' | 'Hostile';

/** Cycle order of a relation cell (server-design 5.4a). */
export const RELATION_CYCLE: readonly Relation[] = ['Allied', 'Neutral', 'Hostile'];

export const nextRelation = (r: Relation): Relation => RELATION_CYCLE[(RELATION_CYCLE.indexOf(r) + 1) % RELATION_CYCLE.length]!;

export const EMPTY_STATE: TeamsStateDto = {
  teams: [],
  members: [],
  unassigned: [],
  relations: { version: 0, entries: [], defaultRelation: 'Neutral' },
  policy: {
    joinMode: 'Auto',
    autoAssign: 'SingleTeam',
    allowCreateInLobby: false,
    lobbyTimeoutSeconds: 300,
    maxTeams: 8,
    maxFactionSlots: 8,
    defaultRelation: 'Neutral',
    assetPolicy: 'SharedCommand',
    allowFriendlyFire: false,
    allowAssetTransfer: false,
    moveAssetsWithPlayer: 'ShipOnly',
    allowSelfTeamChange: false,
    relationChangePolicy: 'AdminOnly',
  },
  sessionPhase: 'Idle',
};

const byId = (a: TeamDto, b: TeamDto) => a.id - b.id;
const byName = (a: TeamMemberDto, b: TeamMemberDto) => a.name.localeCompare(b.name) || a.playerId - b.playerId;

/** Removes a player from both lists and puts it where `member` says (a null team means Unassigned). */
function place(state: TeamsStateDto, member: TeamMemberDto): TeamsStateDto {
  const members = state.members.filter((m) => m.playerId !== member.playerId);
  const unassigned = state.unassigned.filter((m) => m.playerId !== member.playerId);
  if (member.teamId === null) unassigned.push(member);
  else members.push(member);
  return { ...state, members: members.sort(byName), unassigned: unassigned.sort(byName) };
}

/**
 * Applies one hub push of the teams topic to the state. The server sends the whole picture on subscribe (`$snapshot`) and on
 * `TeamsReset`, and patches in between; every patch is idempotent, so a push that overlaps the snapshot does no harm.
 */
export function applyTeamsPush(state: TeamsStateDto, event: string, payload: unknown): TeamsStateDto {
  switch (event) {
    case SNAPSHOT_EVENT:
    case E.TeamsReset:
      return payload as TeamsStateDto;
    case E.TeamUpserted: {
      const team = payload as TeamDto;
      return { ...state, teams: [...state.teams.filter((t) => t.id !== team.id), team].sort(byId) };
    }
    case E.TeamDeleted: {
      const id = Number(payload);
      return { ...state, teams: state.teams.filter((t) => t.id !== id) };
    }
    case E.TeamMemberChanged:
      return place(state, payload as TeamMemberDto);
    case E.PlayerAwaitingTeam: {
      const member = payload as TeamMemberDto;
      return state.members.some((m) => m.playerId === member.playerId) ? state : place(state, member);
    }
    case E.TeamRelationsChanged:
      return { ...state, relations: payload as TeamRelationsDto };
    case E.TeamPolicyChanged:
      return { ...state, policy: payload as TeamPolicyDto };
    default:
      return state;
  }
}

/** A player that is moving (optimistically) to a team, or to Unassigned (null), before the server confirmed. */
export type PendingMoves = ReadonlyMap<number, number | null>;

/** The state with the pending moves applied, so a drop shows at once and rolls back when the server refuses. */
export function withPendingMoves(state: TeamsStateDto, pending: PendingMoves): TeamsStateDto {
  let next = state;
  for (const [playerId, teamId] of pending) {
    const current = [...next.members, ...next.unassigned].find((m) => m.playerId === playerId);
    if (!current || current.teamId === teamId) continue;
    next = place(next, { ...current, teamId, role: 'Member' });
  }
  if (next === state) return state;
  const counts = new Map<number, number>();
  for (const m of next.members) counts.set(m.teamId ?? 0, (counts.get(m.teamId ?? 0) ?? 0) + 1);
  return { ...next, teams: next.teams.map((t) => ({ ...t, memberCount: counts.get(t.id) ?? 0 })) };
}

export const relationOf = (relations: TeamRelationsDto, a: number, b: number): Relation => {
  if (a === b) return 'Allied';
  const lo = Math.min(a, b);
  const hi = Math.max(a, b);
  const hit = relations.entries.find((e) => e.teamA === lo && e.teamB === hi);
  return (hit?.relation ?? relations.defaultRelation) as Relation;
};

/** `lo-hi` key of a pair. */
export const pairKey = (a: number, b: number) => `${Math.min(a, b)}-${Math.max(a, b)}`;
