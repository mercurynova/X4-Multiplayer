import { useRef, useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { ApiError } from '../../api/http';
import { useAuth } from '../../auth/AuthContext';
import type { TeamDto } from '../../generated/generated';
import { E } from '../../hub/contract';
import { PolicyBar } from './PolicyBar';
import { PresetBar } from './PresetBar';
import { RelationGrid } from './RelationGrid';
import { DeleteTeamDialog, TeamFormDialog } from './TeamDialogs';
import { TeamCard } from './TeamCard';
import { teamsApi } from './teamsApi';
import { useTeams } from './useTeams';
import './teams.css';

const TEAM_SLOTS = 8;

export function TeamsPage() {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const { state, error, move, applyPush } = useTeams();
  const dragRef = useRef<number | null>(null);
  const [form, setForm] = useState<{ team?: TeamDto } | null>(null);
  const [deleting, setDeleting] = useState<TeamDto | null>(null);

  if (state === null) {
    return (
      <section className="teams">
        <header className="page-head">
          <h1>Teams &amp; Factions</h1>
        </header>
        {error ? <p role="alert">{error}</p> : <p className="muted">Loading teams…</p>}
      </section>
    );
  }

  const nextSlot = Math.min(
    TEAM_SLOTS,
    Math.max(0, ...state.teams.map((t) => t.factionSlot)) + 1,
  );
  const full = state.teams.length >= Math.min(state.policy.maxTeams, state.policy.maxFactionSlots);
  const setLeader = (team: TeamDto, member: { playerId: number }) =>
    teamsApi.update(team.id, { name: null, color: null, factionSlot: null, leaderPlayerId: member.playerId, locked: null, maxMembers: null, password: null }).catch((e: unknown) =>
      toast('error', e instanceof ApiError ? e.message : 'Could not change the leader.'),
    );

  return (
    <section className="teams">
      <header className="page-head">
        <h1>Teams &amp; Factions</h1>
        <span className="badge">Session {state.sessionPhase}</span>
        {state.unassigned.length > 0 && (
          <span className="badge badge-warn" role="status">
            {state.unassigned.length} unassigned
          </span>
        )}
        {isAdmin && (
          <button type="button" disabled={full} title={full ? 'All team slots are used' : undefined} onClick={() => setForm({})}>
            + New team
          </button>
        )}
      </header>
      {error && <p role="alert">{error}</p>}

      <div className="panel">
        <PresetBar canEdit={isAdmin} />
        <PolicyBar policy={state.policy} canEdit={isAdmin} onChanged={(p) => applyPush(E.TeamPolicyChanged, p)} />
      </div>

      <div className="team-board">
        <TeamCard team={null} members={state.unassigned} teams={state.teams} canEdit={isAdmin} dragRef={dragRef} onMove={(p, t) => void move(p, t)} />
        {state.teams.map((team) => (
          <TeamCard
            key={team.id}
            team={team}
            members={state.members.filter((m) => m.teamId === team.id)}
            teams={state.teams}
            canEdit={isAdmin}
            dragRef={dragRef}
            onMove={(p, t) => void move(p, t)}
            onEdit={(t) => setForm({ team: t })}
            onDelete={setDeleting}
            onLeader={(t, m) => void setLeader(t, m)}
          />
        ))}
      </div>

      <div className="panel">
        <h2>Relations</h2>
        <p className="muted">Symmetric. Click a cell to cycle Allied, Neutral, Hostile, then apply.</p>
        <RelationGrid teams={state.teams} relations={state.relations} canEdit={isAdmin} />
        <div className="row">
          <label>
            Default for pairs nobody set{' '}
            <select
              value={state.policy.defaultRelation}
              disabled={!isAdmin}
              onChange={(e) =>
                void teamsApi
                  .patchPolicy({ defaultRelation: e.target.value })
                  .then((p) => applyPush(E.TeamPolicyChanged, p))
                  .catch((err: unknown) => toast('error', err instanceof ApiError ? err.message : 'Could not save the setting.'))
              }
            >
              <option value="Allied">Allied</option>
              <option value="Neutral">Neutral</option>
              <option value="Hostile">Hostile</option>
            </select>
          </label>
        </div>
      </div>

      {form && (
        <TeamFormDialog team={form.team} nextSlot={nextSlot} onClose={() => setForm(null)} onDone={(m) => toast('success', m)} />
      )}
      {deleting && (
        <DeleteTeamDialog
          team={deleting}
          others={state.teams.filter((t) => t.id !== deleting.id)}
          onClose={() => setDeleting(null)}
          onDone={(m) => toast('success', m)}
        />
      )}
    </section>
  );
}
