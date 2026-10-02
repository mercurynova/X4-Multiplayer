import { useEffect, useRef, useState } from 'react';
import { api } from '../../api/http';
import { useAuth } from '../../auth/AuthContext';
import type { ConnectionStatsDto, LaneStatsDto, MetricSeriesDto } from '../../generated/generated';
import { E, groups } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { problemToFormErrors } from '../../lib/problem';
import { MetricChart } from './MetricChart';
import '../w6.css';

/** A lane whose queue ever grew past this is a reader that fell behind (peak, since the connection started). */
export const SLOW_QUEUE_BYTES = { control: 128 * 1024, realtime: 32 * 1024, bulk: 4 * 1024 * 1024 } as const;

/** Why a connection counts as a slow reader, or null when it keeps up. `droppedSince` = frames dropped since the last push. */
export function slowReason(c: ConnectionStatsDto, droppedSince = 0): string | null {
  if (c.control.maxQueuedBytes >= SLOW_QUEUE_BYTES.control) return `control queue peaked at ${kib(c.control.maxQueuedBytes)}`;
  if (c.realtime.maxQueuedBytes >= SLOW_QUEUE_BYTES.realtime) return `realtime queue peaked at ${kib(c.realtime.maxQueuedBytes)}`;
  if (c.bulk.maxQueuedBytes >= SLOW_QUEUE_BYTES.bulk) return `bulk queue peaked at ${kib(c.bulk.maxQueuedBytes)}`;
  if (droppedSince > 0) return `${droppedSince} frame(s) dropped in the last second`;
  return null;
}

function kib(bytes: number): string {
  return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MiB` : `${(bytes / 1024).toFixed(bytes < 10240 ? 1 : 0)} KiB`;
}

function laneText(l: LaneStatsDto): string {
  return `${kib(l.maxQueuedBytes)}${l.dropped > 0 ? ` / ${l.dropped} dropped` : ''}${l.coalesced > 0 ? ` / ${l.coalesced} coalesced` : ''}`;
}

function age(seconds: number): string {
  if (seconds < 90) return `${Math.round(seconds)} s`;
  if (seconds < 5400) return `${Math.round(seconds / 60)} min`;
  return `${(seconds / 3600).toFixed(1)} h`;
}

/** UDP state from the transport label ("TCP", "TCP+UDP", ...). */
function udpState(transport: string): string {
  return /udp/i.test(transport) ? 'active' : 'TCP only';
}

interface Rates {
  [id: number]: { kbps: number; droppedSince: number };
}

const CHARTS: { title: string; unit: string; series: string[] }[] = [
  { title: 'Bytes out per lane', unit: 'B/s', series: ['net.bytes_out.control', 'net.bytes_out.realtime', 'net.bytes_out.bulk'] },
  { title: 'Send queue per lane', unit: 'B', series: ['net.send_queue_bytes.control', 'net.send_queue_bytes.realtime', 'net.send_queue_bytes.bulk'] },
  { title: 'Dropped and coalesced frames', unit: 'frames/s', series: ['net.dropped', 'net.coalesced'] },
  { title: 'Frames in / out', unit: 'frames/s', series: ['net.frames_in', 'net.frames_out'] },
  { title: 'Connections', unit: '', series: ['net.connections'] },
  { title: 'Working set', unit: 'MB', series: ['process.working_set_mb'] },
];

export function DiagnosticsPage() {
  const { isAdmin } = useAuth();
  const [conns, setConns] = useState<ConnectionStatsDto[]>([]);
  const [rates, setRates] = useState<Rates>({});
  const [error, setError] = useState<string | null>(null);
  const [series, setSeries] = useState<MetricSeriesDto[]>([]);
  const [metricsError, setMetricsError] = useState<string | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [tracing, setTracing] = useState<Record<number, boolean>>({});
  const prev = useRef<{ at: number; byId: Map<number, ConnectionStatsDto> } | null>(null);

  function apply(list: ConnectionStatsDto[]) {
    if (!Array.isArray(list)) return;
    const now = Date.now();
    const p = prev.current;
    const next: Rates = {};
    for (const c of list) {
      const old = p?.byId.get(c.connectionId);
      const dt = p ? (now - p.at) / 1000 : 0;
      next[c.connectionId] = {
        kbps: old && dt > 0 ? Math.max(0, (c.bytesOut - old.bytesOut) / 1000 / dt) : 0,
        droppedSince: old ? Math.max(0, c.dropped - old.dropped) : 0,
      };
    }
    prev.current = { at: now, byId: new Map(list.map((c) => [c.connectionId, c])) };
    setRates(next);
    setConns(list);
  }

  useHubGroup(groups.diagnostics, (event, payload) => {
    if (event === E.Diagnostics) apply(payload as ConnectionStatsDto[]);
  });

  useEffect(() => {
    let cancelled = false;
    api
      .get<ConnectionStatsDto[]>('/api/v1/diagnostics/connections')
      .then((l) => {
        if (!cancelled && !prev.current) apply(l);
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(problemToFormErrors(e).form);
      });
    const loadMetrics = () =>
      api
        .get<MetricSeriesDto[]>('/api/v1/diagnostics/metrics?window=300')
        .then((m) => {
          if (!cancelled && Array.isArray(m)) {
            setSeries(m);
            setMetricsError(null);
          }
        })
        .catch((e: unknown) => {
          if (!cancelled) setMetricsError(problemToFormErrors(e).form);
        });
    void loadMetrics();
    const t = setInterval(() => void loadMetrics(), 5000);
    return () => {
      cancelled = true;
      clearInterval(t);
    };
  }, []);

  async function toggleTrace(id: number, enabled: boolean) {
    try {
      await api.post(`/api/v1/diagnostics/connections/${id}/trace`, { enabled, sampleEvery: 100 });
      setTracing((t) => ({ ...t, [id]: enabled }));
    } catch (e) {
      setError(problemToFormErrors(e).form);
    }
  }

  const slowCount = conns.filter((c) => slowReason(c, rates[c.connectionId]?.droppedSince) !== null).length;
  const sel = conns.find((c) => c.connectionId === selected) ?? null;
  const byName = new Map(series.map((s) => [s.name, s]));

  return (
    <section className="diag-page">
      <h1>Diagnostics</h1>
      {error && <p role="alert">{error}</p>}
      <p className="muted" role="status">
        {conns.length} connection(s){slowCount > 0 ? ` - ${slowCount} slow reader(s)` : ''}
      </p>
      <div className="table-wrap">
        <table>
          <caption className="visually-hidden">Per-connection network statistics, updated every second</caption>
          <thead>
            <tr>
              <th>Conn</th>
              <th>Player</th>
              <th>Roles</th>
              <th>Transport</th>
              <th>UDP</th>
              <th>Age</th>
              <th>RTT</th>
              <th>Control queue</th>
              <th>Realtime queue</th>
              <th>Bulk queue</th>
              <th>Out KB/s</th>
              <th>Dropped</th>
              <th>Coalesced</th>
              <th>Flush avg / max</th>
              <th>Violations</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {conns.length === 0 && (
              <tr>
                <td colSpan={16} className="muted">
                  No connections.
                </td>
              </tr>
            )}
            {conns.map((c) => {
              const reason = slowReason(c, rates[c.connectionId]?.droppedSince);
              return (
                <tr
                  key={c.connectionId}
                  className={`${reason ? 'row-slow' : ''} ${selected === c.connectionId ? 'row-selected' : ''}`}
                  aria-selected={selected === c.connectionId}
                >
                  <td>
                    <button type="button" className="ghost" onClick={() => setSelected(selected === c.connectionId ? null : c.connectionId)}>
                      {c.connectionId}
                    </button>
                  </td>
                  <td>{c.player ?? <span className="muted">(handshake)</span>}</td>
                  <td>{c.roles}</td>
                  <td>{c.transport}</td>
                  <td>{udpState(c.transport)}</td>
                  <td>{age(c.ageSeconds)}</td>
                  <td>{Math.round(c.rttMs)} ms</td>
                  <td>{laneText(c.control)}</td>
                  <td>{laneText(c.realtime)}</td>
                  <td>{laneText(c.bulk)}</td>
                  <td>{(rates[c.connectionId]?.kbps ?? 0).toFixed(1)}</td>
                  <td>{c.dropped}</td>
                  <td>{c.coalesced}</td>
                  <td>
                    {c.flushAvgMs.toFixed(1)} / {c.flushMaxMs.toFixed(1)} ms
                  </td>
                  <td>{c.violations}</td>
                  <td>{reason ? <strong className="slow-flag" title={reason}>Slow reader: {reason}</strong> : <span className="muted">ok</span>}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {sel && (
        <div className="diag-detail">
          <h2>
            Connection {sel.connectionId}
            {sel.player ? ` (${sel.player})` : ''}
          </h2>
          <p className="muted">
            {sel.remote} · in {kib(sel.bytesIn)} / {sel.framesIn} frames · out {kib(sel.bytesOut)} / {sel.framesOut} frames · inbound dropped{' '}
            {sel.inboundDropped}
          </p>
          {isAdmin && (
            <label>
              <input type="checkbox" checked={tracing[sel.connectionId] ?? false} onChange={(e) => void toggleTrace(sel.connectionId, e.target.checked)} />{' '}
              Trace 1 in 100 frames to the log
            </label>
          )}
        </div>
      )}
      <h2>Server metrics (last 5 minutes)</h2>
      {metricsError && <p role="alert">{metricsError}</p>}
      <div className="charts">
        {CHARTS.map((c) => (
          <MetricChart key={c.title} title={c.title} unit={c.unit} series={c.series.flatMap((n) => byName.get(n) ?? [])} />
        ))}
      </div>
    </section>
  );
}
