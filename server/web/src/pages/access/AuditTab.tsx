import { useCallback, useEffect, useMemo, useState } from 'react';
import type { AuditEntryDto } from '../../generated/generated';
import { accessApi, formatTime } from './accessApi';

const LIMITS = [50, 100, 200, 500] as const;

/** The newest audit rows, filterable by actor/action/target/address. Filtering is client-side over the loaded page. */
export function AuditTab() {
  const [rows, setRows] = useState<AuditEntryDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [limit, setLimit] = useState<number>(200);
  const [query, setQuery] = useState('');

  const load = useCallback(() => {
    accessApi
      .audit(limit)
      .then((list) => {
        setRows(list);
        setError(null);
      })
      .catch((err: unknown) => setError(err instanceof Error ? err.message : 'Could not load the audit log.'));
  }, [limit]);

  useEffect(load, [load]);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (rows ?? []).filter(
      (row) =>
        q === '' ||
        [row.actor, row.action, row.target, row.reason, row.remoteIp].some((value) => (value ?? '').toLowerCase().includes(q)),
    );
  }, [rows, query]);

  return (
    <>
      <div className="toolbar">
        <input
          type="search"
          className="search"
          placeholder="Filter by actor, action, target or address"
          aria-label="Filter audit rows"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <label className="inline">
          Show
          <select value={limit} onChange={(e) => setLimit(Number(e.target.value))} aria-label="Rows to load">
            {LIMITS.map((n) => (
              <option key={n} value={n}>
                {n}
              </option>
            ))}
          </select>
        </label>
        <button type="button" className="ghost" onClick={load}>
          Refresh
        </button>
      </div>

      {error && <p role="alert">{error}</p>}
      {rows === null && !error && <p className="muted">Loading audit log…</p>}
      {rows !== null && shown.length === 0 && <p className="muted">No audit rows match.</p>}

      {shown.length > 0 && (
        <div className="table-wrap">
          <table className="data">
            <caption className="visually-hidden">Audit log</caption>
            <thead>
              <tr>
                <th scope="col">Time</th>
                <th scope="col">Actor</th>
                <th scope="col">Action</th>
                <th scope="col">Target</th>
                <th scope="col">Reason</th>
                <th scope="col">Address</th>
                <th scope="col">Data</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((row) => (
                <tr key={row.id}>
                  <td className="nowrap">{formatTime(row.at)}</td>
                  <td>{row.actor}</td>
                  <td>
                    <code>{row.action}</code>
                  </td>
                  <td>{row.target ?? ''}</td>
                  <td>{row.reason ?? ''}</td>
                  <td>{row.remoteIp ?? ''}</td>
                  <td>
                    {row.data && Object.keys(row.data).length > 0 ? (
                      <details>
                        <summary>{Object.keys(row.data).length} field(s)</summary>
                        <dl className="audit-data">
                          {Object.entries(row.data).map(([key, value]) => (
                            <div key={key}>
                              <dt>{key}</dt>
                              <dd>{value}</dd>
                            </div>
                          ))}
                        </dl>
                      </details>
                    ) : (
                      ''
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
