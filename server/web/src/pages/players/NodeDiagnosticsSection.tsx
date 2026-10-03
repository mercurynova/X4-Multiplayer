import { useEffect, useState } from 'react';
import type { NodeDiagnosticsDto } from '../../generated/generated';
import { E, groups } from '../../hub/contract';
import { useHubGroup } from '../../hub/HubProvider';
import { formatDateTime } from './format';
import { playersApi } from './playersApi';

/** Last self-test table and last forwarded log lines of one node: one REST read, then NodeDiagnosticsChanged pushes on the dashboard group. */
export function useNodeDiagnostics(playerId: number) {
  const [diag, setDiag] = useState<NodeDiagnosticsDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    playersApi
      .diagnostics(playerId)
      .then((d) => {
        // A push that arrived first is newer than this read: keep it.
        if (alive) setDiag((cur) => cur ?? d);
      })
      .catch((e: unknown) => {
        if (alive) setError(e instanceof Error ? e.message : 'Could not load the diagnostics.');
      });
    return () => {
      alive = false;
    };
  }, [playerId]);

  useHubGroup(groups.dashboard, (event, payload) => {
    if (event !== E.NodeDiagnosticsChanged) return;
    const d = payload as NodeDiagnosticsDto;
    if (d.playerId === playerId) setDiag(d);
  });

  return { diag, error };
}

function SelfTestTable({ diag }: { diag: NodeDiagnosticsDto }) {
  const table = diag.selfTest;
  if (!table) return <p className="muted">No self-test table received from this player yet.</p>;
  const rows = table.rows ?? [];
  return (
    <>
      <p>
        <span className={`flag ${table.overall === 'FAIL' ? 'flag-ban' : 'flag-ok'}`}>{table.overall}</span> {table.passed} passed, {table.failed} failed, {table.warned} warnings, {table.skipped} skipped (received {formatDateTime(table.at)})
      </p>
      <table className="data" aria-label="Self-test">
        <thead>
          <tr>
            <th scope="col">Check</th>
            <th scope="col">Result</th>
            <th scope="col">Detail</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.name}>
              <th scope="row">{r.name}</th>
              <td className={r.result === 'FAIL' ? 'selftest-fail' : r.result === 'PASS' ? 'selftest-pass' : r.result === 'WARN' ? 'selftest-warn' : 'muted'}>{r.result}</td>
              <td>{r.detail}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

/** The Diagnostics panel of the Player detail page. */
export function NodeDiagnosticsSection({ playerId }: { playerId: number }) {
  const { diag, error } = useNodeDiagnostics(playerId);
  const lines = diag?.lines ?? [];
  return (
    <div className="panel" aria-label="Diagnostics">
      <h2>Diagnostics</h2>
      {error && <p role="alert">{error}</p>}
      {!diag && !error && <p className="muted">Loading…</p>}
      {diag && (
        <>
          <h3>Self-test</h3>
          <SelfTestTable diag={diag} />
          <h3>Last forwarded log lines</h3>
          {lines.length === 0 ? (
            <p className="muted">No log lines forwarded by this player since the server started.</p>
          ) : (
            <>
              {diag.linesDropped > 0 && <p className="muted">{diag.linesDropped} lines were dropped by the rate limit.</p>}
              <pre className="node-log" aria-label="Forwarded log lines">
                {lines.map((l) => `${new Date(l.at).toLocaleTimeString()} ${l.level.padEnd(5)} ${l.text}`).join('\n')}
              </pre>
            </>
          )}
        </>
      )}
    </div>
  );
}
