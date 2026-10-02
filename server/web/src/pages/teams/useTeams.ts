import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import { ApiError } from '../../api/http';
import type { TeamMemberDto, TeamsStateDto } from '../../generated/generated';
import { E, ERROR_EVENT, groups, SNAPSHOT_EVENT } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { teamsApi } from './teamsApi';
import { applyTeamsPush, withPendingMoves, type PendingMoves } from './teamsState';

const messageOf = (e: unknown) => (e instanceof ApiError ? e.message : e instanceof Error ? e.message : 'Could not reach the server.');

/**
 * The live teams picture: one REST read to start from (and for a hub that is not connected yet), then the hub's teams topic
 * (`$snapshot` on every (re)subscribe, patches in between). A move shows at once (optimistic) and rolls back, with a toast,
 * when the server refuses it.
 */
export function useTeams() {
  const { toast } = useAlerts();
  const [server, setServer] = useState<TeamsStateDto | null>(null);
  const [pending, setPending] = useState<PendingMoves>(new Map());
  const [error, setError] = useState<string | null>(null);
  const alive = useRef(true);

  useEffect(() => {
    alive.current = true;
    teamsApi
      .state()
      .then((s) => alive.current && setServer((cur) => cur ?? s))
      .catch((e: unknown) => alive.current && setError(messageOf(e)));
    return () => {
      alive.current = false;
    };
  }, []);

  useHubGroup(groups.teams, (event, payload) => {
    if (event === ERROR_EVENT) {
      setError(messageOf(payload));
      return;
    }
    setError(null);
    setServer((cur) => (event === SNAPSHOT_EVENT || event === E.TeamsReset ? (payload as TeamsStateDto) : cur ? applyTeamsPush(cur, event, payload) : cur));
  });

  const state = useMemo(() => (server ? withPendingMoves(server, pending) : null), [server, pending]);

  const setPendingMove = useCallback((playerId: number, teamId: number | null | undefined) => {
    setPending((p) => {
      const next = new Map(p);
      if (teamId === undefined) next.delete(playerId);
      else next.set(playerId, teamId);
      return next;
    });
  }, []);

  /** Moves a player (null team = Unassigned). Resolves true when the server accepted it. */
  const move = useCallback(
    async (playerId: number, teamId: number | null): Promise<boolean> => {
      setPendingMove(playerId, teamId);
      try {
        const member: TeamMemberDto = await teamsApi.move(playerId, teamId);
        setServer((cur) => (cur ? applyTeamsPush(cur, E.TeamMemberChanged, member) : cur));
        return true;
      } catch (e) {
        toast('error', messageOf(e));
        return false;
      } finally {
        setPendingMove(playerId, undefined);
      }
    },
    [setPendingMove, toast],
  );

  return { state, error, move, applyPush: (event: string, payload: unknown) => setServer((cur) => (cur ? applyTeamsPush(cur, event, payload) : cur)) };
}
