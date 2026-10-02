import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../../api/http';
import { useAuth } from '../../auth/AuthContext';
import type { LogEntryDto, LogFilterDto } from '../../generated/generated';
import { E, groups, SNAPSHOT_EVENT } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { problemToFormErrors } from '../../lib/problem';
import '../w6.css';

const MAX_LINES = 2000;
const LEVELS = ['Verbose', 'Debug', 'Information', 'Warning', 'Error', 'Fatal'] as const;
const ABBREV: Record<string, string> = { verbose: 'VRB', debug: 'DBG', information: 'INF', warning: 'WRN', error: 'ERR', fatal: 'FTL' };

function clip(lines: LogEntryDto[]): LogEntryDto[] {
  return lines.length > MAX_LINES ? lines.slice(lines.length - MAX_LINES) : lines;
}

function stamp(at: string): string {
  const d = new Date(at);
  if (Number.isNaN(d.getTime())) return at;
  const p = (n: number, w = 2) => String(n).padStart(w, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
}

function today(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

function toFilter(level: string, source: string, q: string): LogFilterDto {
  return { level: level || null, source: source.trim() || null, q: q.trim() || null };
}

function queryString(f: LogFilterDto, extra: Record<string, string | number>): string {
  const p = new URLSearchParams();
  if (f.level) p.set('level', f.level);
  if (f.source) p.set('source', f.source);
  if (f.q) p.set('q', f.q);
  for (const [k, v] of Object.entries(extra)) p.set(k, String(v));
  return p.toString();
}

export function LogsPage() {
  const { isAdmin } = useAuth();
  const [level, setLevel] = useState('');
  const [source, setSource] = useState('');
  const [text, setText] = useState('');
  // The server-side subscription follows the filter, but only after typing pauses, so each keystroke does not resubscribe.
  const [filter, setFilter] = useState<LogFilterDto>(() => toFilter('', '', ''));
  useEffect(() => {
    const t = setTimeout(() => setFilter(toFilter(level, source, text)), 300);
    return () => clearTimeout(t);
  }, [level, source, text]);

  const [lines, setLines] = useState<LogEntryDto[]>([]);
  const [paused, setPaused] = useState(false);
  const [pendingCount, setPendingCount] = useState(0);
  const pending = useRef<LogEntryDto[]>([]);
  const pausedRef = useRef(false);
  const [expanded, setExpanded] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sources, setSources] = useState<string[]>([]);
  const [date, setDate] = useState(today);
  const [older, setOlder] = useState(false);
  const [noMore, setNoMore] = useState(false);

  useEffect(() => {
    pausedRef.current = paused;
  }, [paused]);

  const rememberSources = useCallback((entries: readonly LogEntryDto[]) => {
    setSources((cur) => {
      const next = new Set(cur);
      for (const e of entries) if (e.source) next.add(e.source);
      return next.size === cur.length ? cur : [...next].sort();
    });
  }, []);

  useHubGroup(groups.logs(filter), (event, payload) => {
    if (event === SNAPSHOT_EVENT) {
      const backfill = Array.isArray(payload) ? (payload as LogEntryDto[]) : [];
      pending.current = [];
      setPendingCount(0);
      setNoMore(false);
      setLines(clip(backfill));
      rememberSources(backfill);
    } else if (event === E.LogBatch) {
      const batch = payload as LogEntryDto[];
      rememberSources(batch);
      if (pausedRef.current) {
        pending.current = clip([...pending.current, ...batch]);
        setPendingCount(pending.current.length);
      } else {
        setLines((cur) => clip([...cur, ...batch]));
      }
    } else if (event === '$error') {
      setError(problemToFormErrors(payload).form);
    }
  });

  function resume() {
    const flushed = pending.current;
    pending.current = [];
    setLines((cur) => clip([...cur, ...flushed]));
    setPendingCount(0);
    setPaused(false);
  }

  async function loadOlder() {
    const first = lines[0];
    if (!first) return;
    setOlder(true);
    setError(null);
    try {
      const rows = await api.get<LogEntryDto[]>(`/api/v1/logs?${queryString(filter, { before: first.seq, limit: 200 })}`);
      if (Array.isArray(rows)) {
        setLines((cur) => {
          const have = new Set(cur.map((l) => l.seq));
          return [...rows.filter((r) => !have.has(r.seq)), ...cur];
        });
        rememberSources(rows);
        if (rows.length === 0) setNoMore(true);
      }
    } catch (e) {
      setError(problemToFormErrors(e).form);
    } finally {
      setOlder(false);
    }
  }

  const sourceOptions = useMemo(() => ['server', ...sources.filter((s) => s !== 'server')], [sources]);

  return (
    <section className="logs-page">
      <h1>Logs</h1>
      <div className="toolbar">
        <label>
          Level
          <select value={level} onChange={(e) => setLevel(e.target.value)}>
            <option value="">All</option>
            {LEVELS.map((l) => (
              <option key={l} value={l}>
                {l} and above
              </option>
            ))}
          </select>
        </label>
        <label>
          Source
          <input value={source} onChange={(e) => setSource(e.target.value)} list="log-sources" placeholder="any" />
          <datalist id="log-sources">
            {sourceOptions.map((s) => (
              <option key={s} value={s} />
            ))}
          </datalist>
        </label>
        <label>
          Search
          <input value={text} onChange={(e) => setText(e.target.value)} placeholder="message contains" />
        </label>
      </div>
      <div className="toolbar">
        {paused ? (
          <button type="button" onClick={resume}>
            Resume{pendingCount > 0 ? ` (${pendingCount} new)` : ''}
          </button>
        ) : (
          <button type="button" className="ghost" onClick={() => setPaused(true)}>
            Pause
          </button>
        )}
        <button type="button" className="ghost" onClick={() => void loadOlder()} disabled={older || noMore || lines.length === 0}>
          {noMore ? 'No older lines in memory' : 'Load older'}
        </button>
        <button type="button" className="ghost" onClick={() => setLines([])}>
          Clear view
        </button>
        {isAdmin && (
          <>
            <label className="inline-field">
              Day
              <input type="date" value={date} onChange={(e) => setDate(e.target.value)} />
            </label>
            <a className="button-link" href={`/api/v1/logs/download?date=${encodeURIComponent(date)}`} download>
              Download log file
            </a>
          </>
        )}
        <span className="muted spacer-left" role="status">
          {paused ? 'paused' : 'live'} · {lines.length.toLocaleString()} lines
        </span>
      </div>
      {error && <p role="alert">{error}</p>}
      <div className="log-view" role="log" aria-label="Log lines">
        {lines.length === 0 && <p className="muted">No log lines match.</p>}
        {lines.map((l) => (
          <div key={l.seq} className={`log-row log-${l.level.toLowerCase()}`}>
            <button
              type="button"
              className="log-line"
              aria-expanded={expanded === l.seq}
              onClick={() => setExpanded(expanded === l.seq ? null : l.seq)}
            >
              <span className="muted">{stamp(l.at)}</span> <span className="log-level">{ABBREV[l.level.toLowerCase()] ?? l.level}</span>{' '}
              <span className="log-source">{l.source}</span> {l.message}
            </button>
            {expanded === l.seq && (
              <div className="log-detail">
                {l.props && Object.keys(l.props).length > 0 && (
                  <dl>
                    {Object.entries(l.props).map(([k, v]) => (
                      <div key={k}>
                        <dt>{k}</dt>
                        <dd>{v}</dd>
                      </div>
                    ))}
                  </dl>
                )}
                {l.exception && <pre>{l.exception}</pre>}
                {!l.exception && (!l.props || Object.keys(l.props).length === 0) && <span className="muted">No extra details.</span>}
              </div>
            )}
          </div>
        ))}
      </div>
    </section>
  );
}
