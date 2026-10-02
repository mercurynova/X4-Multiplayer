import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { api } from '../../api/http';
import { useAlerts } from '../../alerts/AlertsProvider';
import { useAuth } from '../../auth/AuthContext';
import type {
  CreateSessionRequest,
  SaveDto,
  SessionDetailDto,
  SessionEventDto,
  SessionSummaryDto,
  TransferProgressDto,
} from '../../generated/generated';
import { E, groups } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { problemToFormErrors } from '../../lib/problem';
import { ConfirmDialog } from './ConfirmDialog';
import { formatDateTime, formatDuration } from './format';
import { SavesLibrary } from './SavesLibrary';
import { Transfers } from './Transfers';

const FINISHED_TRANSFER_LINGER_MS = 8_000;
const STOPPABLE = ['WaitingForAuthority', 'AuthorityLoading', 'Running', 'Paused', 'AuthorityLost', 'Migrating'];
const CREATABLE = ['Idle', 'Ended'];

type Pending = 'start' | 'stop' | 'save' | null;

/** The current session: state, uptime, authority, players and the start/stop/request-save actions. */
function CurrentSession({
  session,
  isAdmin,
  onChanged,
}: {
  session: SessionDetailDto | null;
  isAdmin: boolean;
  onChanged: () => void;
}) {
  const { toast } = useAlerts();
  const [pending, setPending] = useState<Pending>(null);
  const [finalSave, setFinalSave] = useState(true);
  const [busy, setBusy] = useState(false);

  // The server sends the uptime with each state push; between pushes it ticks here.
  const [uptime, setUptime] = useState(session?.uptimeSeconds ?? 0);
  useEffect(() => {
    if (!session) return;
    const base = session.uptimeSeconds;
    const t0 = Date.now();
    const running = session.startedAt !== null && session.state !== 'Ended';
    const tick = () => setUptime(running ? base + (Date.now() - t0) / 1000 : base);
    tick();
    if (!running) return;
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  }, [session]);

  if (!session) {
    return (
      <section aria-labelledby="current-h">
        <h2 id="current-h">Current session</h2>
        <p className="muted">No session. Create one below.</p>
      </section>
    );
  }

  async function act(path: string, body: unknown, ok: string) {
    setBusy(true);
    try {
      await api.post(path, body);
      toast('success', ok);
      onChanged();
    } catch (e) {
      toast('error', problemToFormErrors(e).form);
    } finally {
      setBusy(false);
      setPending(null);
    }
  }

  const base = `/api/v1/sessions/${session.id}`;
  const canStart = session.state === 'Idle';
  const canStop = STOPPABLE.includes(session.state);
  const canSave = session.state === 'Running' || session.state === 'Paused';
  return (
    <section aria-labelledby="current-h">
      <h2 id="current-h">Current session</h2>
      <dl className="facts">
        <dt>Name</dt>
        <dd>{session.name}</dd>
        <dt>State</dt>
        <dd>
          <span className={`state state-${session.state.toLowerCase()}`}>{session.state.toUpperCase()}</span>
        </dd>
        <dt>Uptime</dt>
        <dd>{session.startedAt ? formatDuration(uptime) : '-'}</dd>
        <dt>Authority</dt>
        <dd>{session.authority ? `${session.authority.name ?? `player ${session.authority.playerId}`} (${session.authority.status})` : 'none yet'}</dd>
        <dt>Players</dt>
        <dd>{session.players}</dd>
        <dt>Save</dt>
        <dd>{session.saveName ?? 'none selected'}</dd>
      </dl>
      {isAdmin && (
        <div className="row">
          <button type="button" disabled={!canStart || busy} onClick={() => setPending('start')}>
            Start session
          </button>
          <button type="button" disabled={!canSave || busy} onClick={() => setPending('save')}>
            Request save now
          </button>
          <button type="button" className="ghost" disabled={!canStop || busy} onClick={() => setPending('stop')}>
            Stop session
          </button>
          <button type="button" className="ghost" disabled title="Authority migration arrives in a later milestone">
            Promote authority
          </button>
        </div>
      )}
      {pending === 'start' && (
        <ConfirmDialog
          title="Start this session?"
          confirmLabel="Start"
          busy={busy}
          onCancel={() => setPending(null)}
          onConfirm={() => void act(`${base}/start`, {}, 'Session starting; waiting for the authority.')}
        >
          <p>The first node to connect as authority loads the selected save; other players are admitted after it.</p>
        </ConfirmDialog>
      )}
      {pending === 'save' && (
        <ConfirmDialog
          title="Ask the authority to save now?"
          confirmLabel="Request save"
          busy={busy}
          onCancel={() => setPending(null)}
          onConfirm={() => void act(`${base}/request-save`, undefined, 'Save requested.')}
        >
          <p>The authority saves and uploads the file; the game may hitch for a moment.</p>
        </ConfirmDialog>
      )}
      {pending === 'stop' && (
        <ConfirmDialog
          title="Stop the session?"
          confirmLabel="Stop session"
          danger
          busy={busy}
          onCancel={() => setPending(null)}
          onConfirm={() => void act(`${base}/stop`, { requestFinalSave: finalSave }, 'Session stopping.')}
        >
          <label>
            <input type="checkbox" checked={finalSave} onChange={(e) => setFinalSave(e.target.checked)} /> Take a final save first
          </label>
          <p>All players are disconnected from the session.</p>
        </ConfirmDialog>
      )}
    </section>
  );
}

function NewSession({
  saves,
  selectedSha,
  onSelect,
  canCreate,
  onChanged,
}: {
  saves: readonly SaveDto[];
  selectedSha: string | null;
  onSelect: (sha: string | null) => void;
  canCreate: boolean;
  onChanged: () => void;
}) {
  const { toast } = useAlerts();
  const [name, setName] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent, thenStart: boolean) {
    e.preventDefault();
    setBusy(true);
    setErrors({});
    setFormError(null);
    try {
      const body: CreateSessionRequest = { name: name.trim(), saveId: selectedSha, settings: null };
      const created = await api.post<SessionDetailDto>('/api/v1/sessions', body);
      if (thenStart) await api.post(`/api/v1/sessions/${created.id}/start`, {});
      toast('success', thenStart ? `Session ${created.name} created and starting.` : `Session ${created.name} created.`);
      setName('');
      onChanged();
    } catch (err) {
      const p = problemToFormErrors(err);
      setErrors(p.fields);
      setFormError(p.form);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="new-h">
      <h2 id="new-h">New session</h2>
      <form onSubmit={(e) => void submit(e, false)} className="form-grid">
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} maxLength={64} aria-invalid={errors.name ? true : undefined} />
          {errors.name && <span className="field-error">{errors.name}</span>}
        </label>
        <label>
          Save
          <select value={selectedSha ?? ''} onChange={(e) => onSelect(e.target.value || null)}>
            <option value="">(none: the authority's own save)</option>
            {saves.map((s) => (
              <option key={s.sha256} value={s.sha256}>
                {s.displayName}
              </option>
            ))}
          </select>
          {errors.saveId && <span className="field-error">{errors.saveId}</span>}
        </label>
        {!canCreate && <p className="muted">A session is active. Stop it before creating another.</p>}
        <div className="row">
          <button type="submit" disabled={busy || !canCreate || name.trim() === ''}>
            Create
          </button>
          <button type="button" disabled={busy || !canCreate || name.trim() === ''} onClick={(e) => void submit(e, true)}>
            Create and start
          </button>
        </div>
        {formError && Object.keys(errors).length === 0 && (
          <p className="field-error" role="alert">
            {formError}
          </p>
        )}
      </form>
    </section>
  );
}

function History({ sessions }: { sessions: readonly SessionSummaryDto[] }) {
  const [openId, setOpenId] = useState<number | null>(null);
  const [events, setEvents] = useState<SessionEventDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  function toggle(id: number) {
    if (openId === id) {
      setOpenId(null);
      return;
    }
    setOpenId(id);
    setEvents(null);
    setError(null);
    api
      .get<SessionEventDto[]>(`/api/v1/sessions/${id}/events?limit=100`)
      .then(setEvents)
      .catch((e: unknown) => setError(problemToFormErrors(e).form));
  }

  return (
    <section aria-labelledby="history-h">
      <h2 id="history-h">Session history</h2>
      {sessions.length === 0 ? (
        <p className="muted">No sessions yet.</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>State</th>
                <th>Started</th>
                <th>Duration</th>
                <th>Players</th>
                <th>Save</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {sessions.flatMap((s) => [
                <tr key={s.id}>
                  <td>{s.name}</td>
                  <td>{s.state}</td>
                  <td>{formatDateTime(s.startedAt)}</td>
                  <td>{s.startedAt ? formatDuration(s.uptimeSeconds) : '-'}</td>
                  <td>{s.players}</td>
                  <td>{s.saveName ?? '-'}</td>
                  <td>
                    <button type="button" className="ghost" aria-expanded={openId === s.id} onClick={() => toggle(s.id)}>
                      {openId === s.id ? 'Hide events' : 'View events'}
                    </button>
                  </td>
                </tr>,
                openId === s.id && (
                  <tr key={`${s.id}-events`}>
                    <td colSpan={7}>
                      {error && <p className="field-error">{error}</p>}
                      {!error && events === null && <p className="muted">Loading...</p>}
                      {events && events.length === 0 && <p className="muted">No events recorded.</p>}
                      {events && events.length > 0 && (
                        <ul className="events">
                          {events.map((ev) => (
                            <li key={ev.id}>
                              <span className="muted">{formatDateTime(ev.at)}</span> {ev.type}
                            </li>
                          ))}
                        </ul>
                      )}
                    </td>
                  </tr>
                ),
              ])}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/** Sessions & Saves screen (server-design 5.6). */
export function SessionsPage() {
  const { isAdmin } = useAuth();
  const { toast } = useAlerts();
  const [current, setCurrent] = useState<SessionDetailDto | null | undefined>(undefined);
  const [history, setHistory] = useState<SessionSummaryDto[]>([]);
  const [saves, setSaves] = useState<SaveDto[]>([]);
  const [selectedSha, setSelectedSha] = useState<string | null>(null);
  const [transfers, setTransfers] = useState<TransferProgressDto[]>([]);
  const timers = useRef(new Set<ReturnType<typeof setTimeout>>());

  // The server's save catalog is write-behind: a list right after an upload or delete can still show the old state. The page
  // therefore remembers what it just added/removed and applies that on top of the list until the server agrees.
  const [localAdds, setLocalAdds] = useState<SaveDto[]>([]);
  const [localRemoves, setLocalRemoves] = useState<string[]>([]);

  const refresh = useCallback(() => {
    const fail = (e: unknown) => toast('error', problemToFormErrors(e).form);
    api.get<SessionDetailDto | undefined>('/api/v1/sessions/current').then((s) => setCurrent(s ?? null)).catch(fail);
    api.get<SessionSummaryDto[]>('/api/v1/sessions?limit=50').then(setHistory).catch(fail);
    api
      .get<SaveDto[]>('/api/v1/saves')
      .then((list) => {
        setSaves(list);
        const shas = new Set(list.map((s) => s.sha256));
        setLocalAdds((a) => (a.some((x) => shas.has(x.sha256)) ? a.filter((x) => !shas.has(x.sha256)) : a));
        setLocalRemoves((r) => (r.some((x) => !shas.has(x)) ? r.filter((x) => shas.has(x)) : r));
      })
      .catch(fail);
  }, [toast]);

  /** Refresh now and once more shortly after, for the write-behind lag. */
  const refreshSoon = useCallback(() => {
    refresh();
    const timer = setTimeout(() => {
      timers.current.delete(timer);
      refresh();
    }, 1500);
    timers.current.add(timer);
  }, [refresh]);

  const shownSaves = [...localAdds.filter((a) => !saves.some((s) => s.sha256 === a.sha256)), ...saves].filter(
    (s) => !localRemoves.includes(s.sha256),
  );

  useEffect(() => {
    refresh();
    const pending = timers.current;
    return () => pending.forEach(clearTimeout);
  }, [refresh]);

  useHubGroup(groups.dashboard, (event, payload) => {
    if (event === E.SessionChanged) {
      refresh();
    } else if (event === E.SaveTransfer) {
      const t = payload as TransferProgressDto;
      setTransfers((list) => [...list.filter((x) => x.id !== t.id), t]);
      if (t.finished) {
        // The finished row stays briefly, then the saves list picks up what an upload stored.
        const timer = setTimeout(() => {
          timers.current.delete(timer);
          setTransfers((list) => list.filter((x) => x.id !== t.id));
        }, FINISHED_TRANSFER_LINGER_MS);
        timers.current.add(timer);
        if (t.isUpload) refresh();
      }
    }
  });

  const canCreate = current == null || CREATABLE.includes(current.state);

  return (
    <section>
      <h1>Sessions &amp; Saves</h1>
      {current === undefined ? <p className="muted">Loading...</p> : <CurrentSession session={current} isAdmin={isAdmin} onChanged={refresh} />}
      {isAdmin && (
        <NewSession saves={shownSaves} selectedSha={selectedSha} onSelect={setSelectedSha} canCreate={canCreate} onChanged={refresh} />
      )}
      <SavesLibrary
        saves={shownSaves}
        isAdmin={isAdmin}
        selectedSha={selectedSha}
        onSelect={setSelectedSha}
        onChanged={refreshSoon}
        onUploaded={(s) => setLocalAdds((a) => [...a.filter((x) => x.sha256 !== s.sha256), s])}
        onDeleted={(sha) => setLocalRemoves((r) => [...r, sha])}
      />
      <Transfers transfers={transfers} />
      <History sessions={history} />
    </section>
  );
}
