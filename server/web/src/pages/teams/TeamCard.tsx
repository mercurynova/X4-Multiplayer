import { useRef, useState } from 'react';
import type { TeamDto, TeamMemberDto } from '../../generated/generated';

export interface TeamCardProps {
  /** The team, or null for the Unassigned pool. */
  team: TeamDto | null;
  members: readonly TeamMemberDto[];
  /** Every team a member can be moved to (the Unassigned pool is added by the card). */
  teams: readonly TeamDto[];
  canEdit: boolean;
  /** The member being dragged (shared by all cards of the page). */
  dragRef: { current: number | null };
  onMove: (playerId: number, teamId: number | null) => void;
  onEdit?: (team: TeamDto) => void;
  onDelete?: (team: TeamDto) => void;
  onLeader?: (team: TeamDto, member: TeamMemberDto) => void;
}

function MemberRow({
  member,
  team,
  teams,
  canEdit,
  dragRef,
  onMove,
  onLeader,
}: { member: TeamMemberDto; team: TeamDto | null } & Pick<TeamCardProps, 'teams' | 'canEdit' | 'dragRef' | 'onMove' | 'onLeader'>) {
  const isLeader = team !== null && team.leaderPlayerId === member.playerId;
  return (
    <li
      className="member"
      data-player={member.name}
      draggable={canEdit}
      onDragStart={(e) => {
        dragRef.current = member.playerId;
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', String(member.playerId));
      }}
      onDragEnd={() => {
        dragRef.current = null;
      }}
    >
      {canEdit && (
        <span className="grip" aria-hidden="true" title="Drag onto a team">
          ⠿
        </span>
      )}
      {team !== null &&
        (canEdit ? (
          <button
            type="button"
            className={`ghost star${isLeader ? ' on' : ''}`}
            aria-pressed={isLeader}
            aria-label={`${isLeader ? 'Leader' : 'Make leader'}: ${member.name}`}
            onClick={() => !isLeader && team && onLeader?.(team, member)}
          >
            {isLeader ? '★' : '☆'}
          </button>
        ) : (
          isLeader && <span aria-label="Leader">★</span>
        ))}
      <span className="member-name">{member.name}</span>
      {member.isAuthority && <span className="badge">authority</span>}
      <span className={member.online ? 'presence-on' : 'presence-off'}>
        <span aria-hidden="true">{member.online ? '●' : '○'}</span>
        <span className="visually-hidden">{member.online ? 'online' : 'offline'}</span>
        {team === null && member.online && ' waiting'}
      </span>
      {canEdit && (
        <select
          className="move-select"
          aria-label={`Move ${member.name} to`}
          value=""
          onChange={(e) => {
            const v = e.target.value;
            if (v === '') return;
            onMove(member.playerId, v === 'none' ? null : Number(v));
          }}
        >
          <option value="">Move to…</option>
          {teams
            .filter((t) => t.id !== team?.id)
            .map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          {team !== null && <option value="none">Unassigned</option>}
        </select>
      )}
    </li>
  );
}

/** One team (or the Unassigned pool): header, members (draggable, with a Move-to menu as the keyboard alternative) and a drop zone. */
export function TeamCard({ team, members, teams, canEdit, dragRef, onMove, onEdit, onDelete, onLeader }: TeamCardProps) {
  const [over, setOver] = useState(false);
  const label = team ? `Team ${team.name}` : 'Unassigned';
  const depth = useRef(0);
  const full = team?.maxMembers != null && members.length >= team.maxMembers;
  return (
    <article
      className={`team-card${over ? ' drop-over' : ''}${team === null ? ' unassigned' : ''}`}
      aria-label={label}
      data-team={team?.name ?? 'Unassigned'}
      style={team ? { borderTopColor: team.color } : undefined}
      onDragEnter={() => {
        depth.current++;
        if (canEdit && dragRef.current !== null) setOver(true);
      }}
      onDragLeave={() => {
        depth.current = Math.max(0, depth.current - 1);
        if (depth.current === 0) setOver(false);
      }}
      onDragOver={(e) => {
        if (canEdit && dragRef.current !== null) {
          e.preventDefault();
          e.dataTransfer.dropEffect = 'move';
        }
      }}
      onDrop={(e) => {
        e.preventDefault();
        depth.current = 0;
        setOver(false);
        const id = dragRef.current ?? Number(e.dataTransfer.getData('text/plain'));
        dragRef.current = null;
        if (Number.isFinite(id) && id > 0 && !members.some((m) => m.playerId === id)) onMove(id, team?.id ?? null);
      }}
    >
      <header>
        {team ? (
          <>
            <span className="swatch" style={{ background: team.color }} aria-hidden="true" />
            <h3>{team.name}</h3>
            <span className="muted">slot {team.factionSlot}</span>
            {team.locked && <span className="badge" title="Players cannot choose this team in the lobby">locked</span>}
            {team.hasPassword && <span className="badge">password</span>}
          </>
        ) : (
          <h3>Unassigned ({members.length})</h3>
        )}
        {team && canEdit && (
          <span className="card-actions">
            <button type="button" className="ghost" aria-label={`Edit ${team.name}`} onClick={() => onEdit?.(team)}>
              Edit
            </button>
            <button type="button" className="ghost" aria-label={`Delete ${team.name}`} onClick={() => onDelete?.(team)}>
              Delete
            </button>
          </span>
        )}
      </header>
      {members.length === 0 ? (
        <p className="muted empty">{team ? 'No members. Drop a player here.' : 'Nobody is waiting.'}</p>
      ) : (
        <ul>
          {members.map((m) => (
            <MemberRow key={m.playerId} member={m} team={team} teams={teams} canEdit={canEdit} dragRef={dragRef} onMove={onMove} onLeader={onLeader} />
          ))}
        </ul>
      )}
      <footer className="muted">
        {team
          ? `${members.length}/${team.maxMembers ?? '∞'} members${full ? ' (full)' : ''}`
          : canEdit
            ? 'Drag a player onto a team'
            : ''}
      </footer>
    </article>
  );
}
