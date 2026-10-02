import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { api } from '../../api/http';
import { useAuth } from '../../auth/AuthContext';
import type { ChatMessageDto, ChatSentDto, PlayerDto, SendChatRequest } from '../../generated/generated';
import { E, groups } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { problemToFormErrors } from '../../lib/problem';
import '../w6.css';

const MAX_LINES = 500;
const PAGE = 100;
const dedupeKey = (m: ChatMessageDto) => `${m.at}|${m.from}|${m.channel}|${m.text}`;
const byTime = (a: ChatMessageDto, b: ChatMessageDto) => Date.parse(a.at) - Date.parse(b.at) || a.id - b.id;

/** Merge two lists, dropping duplicates (a live line and its stored copy differ only in the id), oldest first. */
export function mergeChat(existing: readonly ChatMessageDto[], incoming: readonly ChatMessageDto[]): ChatMessageDto[] {
  const seen = new Map<string, ChatMessageDto>();
  for (const m of [...incoming, ...existing]) {
    const k = dedupeKey(m);
    const prev = seen.get(k);
    // Prefer the stored copy (positive id) over the live one (negative local id).
    if (!prev || (prev.id < 0 && m.id > 0)) seen.set(k, m);
  }
  return [...seen.values()].sort(byTime).slice(-MAX_LINES);
}

function time(at: string): string {
  const d = new Date(at);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function label(m: ChatMessageDto): string {
  if (m.channel.toLowerCase() === 'system') return '[SYSTEM]';
  if (m.fromAdmin) return `[ADMIN] ${m.from}`;
  return m.from;
}

export function ChatPage() {
  const { isAdmin } = useAuth();
  const [messages, setMessages] = useState<ChatMessageDto[]>([]);
  const [players, setPlayers] = useState<PlayerDto[]>([]);
  const [channel, setChannel] = useState('all');
  const [loadError, setLoadError] = useState<string | null>(null);
  const [exhausted, setExhausted] = useState(false);

  const [text, setText] = useState('');
  const [target, setTarget] = useState<'all' | 'player'>('all');
  const [toPlayerId, setToPlayerId] = useState('');
  const [asBroadcast, setAsBroadcast] = useState(false);
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [status, setStatus] = useState<string | null>(null);
  const logRef = useRef<HTMLDivElement>(null);

  useHubGroup(groups.chat, (event, payload) => {
    if (event === E.Chat) setMessages((cur) => mergeChat(cur, [payload as ChatMessageDto]));
  });

  useEffect(() => {
    let cancelled = false;
    api
      .get<ChatMessageDto[]>(`/api/v1/chat?limit=${PAGE}`)
      .then((rows) => {
        if (cancelled || !Array.isArray(rows)) return;
        setMessages((cur) => mergeChat(cur, rows));
        setExhausted(rows.length < PAGE);
      })
      .catch((e: unknown) => {
        if (!cancelled) setLoadError(problemToFormErrors(e).form);
      });
    api
      .get<PlayerDto[]>('/api/v1/players')
      .then((rows) => {
        if (!cancelled && Array.isArray(rows)) setPlayers(rows);
      })
      .catch(() => undefined); // the player picker and mute links are optional
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const el = logRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages, channel]);

  const loadOlder = useCallback(async () => {
    const oldest = messages.reduce<number | null>((min, m) => (m.id > 0 && (min === null || m.id < min) ? m.id : min), null);
    if (oldest === null) return;
    try {
      const rows = await api.get<ChatMessageDto[]>(`/api/v1/chat?limit=${PAGE}&before=${oldest}`);
      if (!Array.isArray(rows)) return;
      setMessages((cur) => mergeChat(cur, rows));
      if (rows.length < PAGE) setExhausted(true);
    } catch (e) {
      setLoadError(problemToFormErrors(e).form);
    }
  }, [messages]);

  const channels = useMemo(() => ['all', ...new Set(messages.map((m) => m.channel))], [messages]);
  const shown = channel === 'all' ? messages : messages.filter((m) => m.channel === channel);
  const idByName = useMemo(() => new Map(players.map((p) => [p.name, p.id])), [players]);

  async function send(e: FormEvent) {
    e.preventDefault();
    if (!text.trim()) return;
    setSending(true);
    setSendError(null);
    setFieldErrors({});
    setStatus(null);
    const body: SendChatRequest = {
      text: text.trim(),
      channel: target,
      toPlayerId: target === 'player' && toPlayerId ? Number(toPlayerId) : null,
      asBroadcast: target === 'all' && asBroadcast,
    };
    try {
      const res = await api.post<ChatSentDto>('/api/v1/chat', body);
      setText('');
      setStatus(`Delivered to ${res?.delivered ?? 0} player(s).`);
    } catch (err) {
      const fe = problemToFormErrors(err);
      setFieldErrors(fe.fields);
      setSendError(fe.form);
    } finally {
      setSending(false);
    }
  }

  return (
    <section className="chat-page">
      <h1>Chat</h1>
      {loadError && <p role="alert">{loadError}</p>}
      <label className="inline-field">
        Channel
        <select value={channel} onChange={(e) => setChannel(e.target.value)}>
          {channels.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
      </label>
      {!exhausted && messages.some((m) => m.id > 0) && (
        <button type="button" className="ghost" onClick={() => void loadOlder()}>
          Load older messages
        </button>
      )}
      <div className="chat-log" ref={logRef} role="log" aria-label="Chat messages" aria-live="polite">
        {shown.length === 0 && <p className="muted">No messages yet.</p>}
        {shown.map((m) => {
          const pid = m.fromAdmin ? undefined : idByName.get(m.from);
          return (
            <div key={`${m.id}|${dedupeKey(m)}`} className={`chat-line chat-${m.channel.toLowerCase()}`}>
              <span className="muted">{time(m.at)}</span> <strong>{label(m)}</strong>
              {pid !== undefined && (
                <>
                  {' '}
                  <Link to={`/players/${pid}`} className="mute-link" aria-label={`Open ${m.from} to mute`}>
                    mute
                  </Link>
                </>
              )}
              : {m.text}
            </div>
          );
        })}
      </div>
      {isAdmin ? (
        <form className="chat-send" onSubmit={(e) => void send(e)} noValidate>
          <fieldset>
            <legend>To</legend>
            <label>
              <input type="radio" name="target" checked={target === 'all'} onChange={() => setTarget('all')} /> Everyone
            </label>
            <label>
              <input type="radio" name="target" checked={target === 'player'} onChange={() => setTarget('player')} /> Player
            </label>
            {target === 'player' && (
              <label>
                Player
                <select value={toPlayerId} onChange={(e) => setToPlayerId(e.target.value)} aria-invalid={!!fieldErrors.toPlayerId}>
                  <option value="">Choose...</option>
                  {players.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                      {p.online ? '' : ' (offline)'}
                    </option>
                  ))}
                </select>
                {fieldErrors.toPlayerId && <span className="field-error">{fieldErrors.toPlayerId}</span>}
              </label>
            )}
            {target === 'all' && (
              <label>
                <input type="checkbox" checked={asBroadcast} onChange={(e) => setAsBroadcast(e.target.checked)} /> Show as on-screen
                broadcast banner
              </label>
            )}
          </fieldset>
          <label>
            Message
            <input value={text} onChange={(e) => setText(e.target.value)} maxLength={256} aria-invalid={!!fieldErrors.text} />
            {fieldErrors.text && <span className="field-error">{fieldErrors.text}</span>}
          </label>
          <button type="submit" disabled={sending || !text.trim()}>
            Send
          </button>
          {sendError && <p role="alert">{sendError}</p>}
          {status && (
            <p className="notice" role="status">
              {status}
            </p>
          )}
        </form>
      ) : (
        <p className="muted">Viewers can read chat but not send.</p>
      )}
    </section>
  );
}
