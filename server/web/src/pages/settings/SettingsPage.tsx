import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { ApiError, api } from '../../api/http';
import { useAuth } from '../../auth/AuthContext';
import type { SettingSchemaDto, SettingsDto, SettingsSchemaDto } from '../../generated/generated';
import { E } from '../../hub/contract';
import { useHubEvent } from '../../hub/HubProvider';
import { problemToFormErrors } from '../../lib/problem';
import '../w6.css';

type Draft = Record<string, string | boolean>;

/** Editor value (string/bool) for a setting's current server value. Secrets start empty: the server only sends a mask. */
export function toEditor(s: SettingSchemaDto, value: unknown): string | boolean {
  if (s.secret) return '';
  if (s.type === 'bool') return value === true;
  if (s.type === 'stringList') return Array.isArray(value) ? value.join('\n') : '';
  return value === null || value === undefined ? '' : String(value);
}

/** Editor value back to the JSON the PATCH wants, or an error message when the text does not fit the type. */
export function fromEditor(s: SettingSchemaDto, v: string | boolean): { value: unknown } | { error: string } {
  switch (s.type) {
    case 'bool':
      return { value: v === true };
    case 'int':
    case 'long': {
      const n = Number(String(v).trim());
      return String(v).trim() === '' || !Number.isInteger(n) ? { error: 'Enter a whole number.' } : { value: n };
    }
    case 'double': {
      const n = Number(String(v).trim());
      return String(v).trim() === '' || !Number.isFinite(n) ? { error: 'Enter a number.' } : { value: n };
    }
    case 'stringList':
      return { value: String(v).split('\n').map((x) => x.trim()).filter((x) => x.length > 0) };
    default:
      return { value: String(v) };
  }
}

function describeDefault(s: SettingSchemaDto): string {
  const d = s.default;
  if (d === null || d === undefined || d === '') return '(empty)';
  if (Array.isArray(d)) return d.length === 0 ? '(empty list)' : d.join(', ');
  return String(d);
}

function hint(s: SettingSchemaDto): string | null {
  if (s.min !== null && s.max !== null) return `${s.min} to ${s.max}`;
  if (s.min !== null) return `at least ${s.min}`;
  if (s.max !== null) return `at most ${s.max}`;
  if (s.maxLength !== null) return `up to ${s.maxLength} characters`;
  return null;
}

export function SettingsPage() {
  const { isAdmin } = useAuth();
  const [schema, setSchema] = useState<SettingSchemaDto[] | null>(null);
  const [values, setValues] = useState<SettingsDto | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [restartKeys, setRestartKeys] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    Promise.all([api.get<SettingsSchemaDto>('/api/v1/settings/schema'), api.get<SettingsDto>('/api/v1/settings')])
      .then(([sc, v]) => {
        if (cancelled) return;
        setSchema(Array.isArray(sc?.settings) ? sc.settings : []);
        setValues(v);
      })
      .catch((e: unknown) => {
        if (!cancelled) setLoadError(problemToFormErrors(e).form);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // A change made elsewhere (another admin, a reset) replaces the shown values; unsaved edits stay.
  useHubEvent(E.SettingsChanged, (p) => {
    const next = p as SettingsDto;
    if (next && next.sections) setValues(next);
  });

  // `values` is keyed section -> property name; the schema's `name` is the display text, the key is "Section.Property".
  const valueOf = useCallback(
    (s: SettingSchemaDto): unknown => values?.sections?.[s.section]?.[s.key.slice(s.section.length + 1)],
    [values],
  );

  const sections = useMemo(() => {
    const map = new Map<string, SettingSchemaDto[]>();
    for (const s of schema ?? []) {
      const list = map.get(s.section) ?? [];
      list.push(s);
      map.set(s.section, list);
    }
    return [...map.entries()];
  }, [schema]);

  const overrides = useMemo(() => new Set(values?.overrides ?? []), [values]);

  /** Keys whose editor value differs from the current server value. */
  const dirtyKeys = useMemo(() => {
    const bySchemaKey = new Map((schema ?? []).map((s) => [s.key, s]));
    return Object.keys(draft).filter((k) => {
      const s = bySchemaKey.get(k);
      return s && draft[k] !== toEditor(s, valueOf(s));
    });
  }, [draft, schema, valueOf]);

  function edit(s: SettingSchemaDto, v: string | boolean) {
    setSaved(null);
    setErrors((e) => {
      if (!(s.key in e)) return e;
      const { [s.key]: _drop, ...rest } = e;
      void _drop;
      return rest;
    });
    setDraft((d) => ({ ...d, [s.key]: v }));
  }

  /** Applies a PATCH and maps a 400 onto the per-key errors. Returns true on success. */
  async function patch(body: Record<string, unknown>): Promise<boolean> {
    setSaving(true);
    setFormError(null);
    setSaved(null);
    try {
      const next = await api.patch<SettingsDto>('/api/v1/settings', body);
      if (next && next.sections) setValues(next);
      return true;
    } catch (e) {
      if (e instanceof ApiError && e.errors) {
        const keyed: Record<string, string> = {};
        for (const [k, msgs] of Object.entries(e.errors)) keyed[k] = msgs[0] ?? 'Invalid value.';
        setErrors(keyed);
        setRestartKeys(Object.entries(e.errorCodes ?? {}).filter(([, c]) => c === 'RestartRequired').map(([k]) => k));
        setFormError(e.message);
      } else {
        setFormError(problemToFormErrors(e).form);
      }
      return false;
    } finally {
      setSaving(false);
    }
  }

  async function save(ev: FormEvent) {
    ev.preventDefault();
    const local: Record<string, string> = {};
    const body: Record<string, unknown> = {};
    for (const s of schema ?? []) {
      if (!dirtyKeys.includes(s.key)) continue;
      const r = fromEditor(s, draft[s.key] as string | boolean);
      if ('error' in r) local[s.key] = r.error;
      else body[s.key] = r.value;
    }
    setErrors(local);
    setRestartKeys([]);
    if (Object.keys(local).length > 0) {
      setFormError('Fix the highlighted settings, then save again.');
      return;
    }
    if (Object.keys(body).length === 0) return;
    if (await patch(body)) {
      setDraft({});
      setErrors({});
      setSaved(`Saved ${Object.keys(body).length} setting(s).`);
    }
  }

  async function reset(s: SettingSchemaDto) {
    setErrors((e) => {
      const { [s.key]: _drop, ...rest } = e;
      void _drop;
      return rest;
    });
    if (await patch({ [s.key]: null })) {
      setDraft((d) => {
        const { [s.key]: _drop, ...rest } = d;
        void _drop;
        return rest;
      });
      setSaved(`${s.name} reset to its default.`);
    }
  }

  if (loadError) {
    return (
      <section>
        <h1>Settings</h1>
        <p role="alert">{loadError}</p>
      </section>
    );
  }
  if (!schema || !values) {
    return (
      <section>
        <h1>Settings</h1>
        <p className="muted">Loading settings...</p>
      </section>
    );
  }

  const hasBoot = schema.some((s) => s.requiresRestart);

  return (
    <section className="settings-page">
      <h1>Settings</h1>
      {restartKeys.length > 0 && (
        <p className="notice" role="status">
          Restart required: {restartKeys.join(', ')} can only change in appsettings.json followed by a server restart.
        </p>
      )}
      {hasBoot && (
        <p className="muted">
          Settings marked <span className="badge">Boot - restart required</span> are read-only here: edit appsettings.json and restart the
          server. <span className="badge">Live</span> settings apply at once.
        </p>
      )}
      <nav aria-label="Settings sections">
        {sections.map(([name]) => (
          <a key={name} href={`#settings-${name}`} className="section-link">
            {name}
          </a>
        ))}
      </nav>
      <form onSubmit={(e) => void save(e)} noValidate>
        {sections.map(([name, list]) => (
          <fieldset key={name} id={`settings-${name}`}>
            <legend>{name}</legend>
            {list.map((s) => {
              const readOnly = !isAdmin || s.requiresRestart;
              const err = errors[s.key];
              const id = `setting-${s.key}`;
              const current = draft[s.key] ?? toEditor(s, valueOf(s));
              const maskedSet = s.secret && typeof valueOf(s) === 'string' && (valueOf(s) as string).length > 0;
              const h = hint(s);
              return (
                <div key={s.key} className="setting-row">
                  <div className="setting-head">
                    <label htmlFor={id}>{s.name}</label>{' '}
                    {s.requiresRestart ? (
                      <span className="badge badge-boot" title="Edit appsettings.json and restart the server">
                        Boot - restart required
                      </span>
                    ) : (
                      <span className="badge badge-live">Live</span>
                    )}
                    {overrides.has(s.key) && <span className="badge">changed</span>}
                  </div>
                  {s.type === 'bool' ? (
                    <input
                      id={id}
                      type="checkbox"
                      className="setting-check"
                      checked={current === true}
                      disabled={readOnly}
                      aria-describedby={`${id}-help`}
                      aria-invalid={!!err}
                      onChange={(e) => edit(s, e.target.checked)}
                    />
                  ) : s.type === 'enum' ? (
                    <select
                      id={id}
                      value={String(current)}
                      disabled={readOnly}
                      aria-describedby={`${id}-help`}
                      aria-invalid={!!err}
                      onChange={(e) => edit(s, e.target.value)}
                    >
                      {(s.values ?? []).map((v) => (
                        <option key={v} value={v}>
                          {v}
                        </option>
                      ))}
                    </select>
                  ) : s.type === 'stringList' ? (
                    <textarea
                      id={id}
                      rows={3}
                      value={String(current)}
                      disabled={readOnly}
                      aria-describedby={`${id}-help`}
                      aria-invalid={!!err}
                      onChange={(e) => edit(s, e.target.value)}
                    />
                  ) : (
                    <input
                      id={id}
                      type={s.secret ? 'password' : 'text'}
                      inputMode={s.type === 'int' || s.type === 'long' || s.type === 'double' ? 'decimal' : undefined}
                      autoComplete={s.secret ? 'new-password' : 'off'}
                      value={String(current)}
                      placeholder={maskedSet ? '(set - type to change)' : undefined}
                      disabled={readOnly}
                      aria-describedby={`${id}-help`}
                      aria-invalid={!!err}
                      onChange={(e) => edit(s, e.target.value)}
                    />
                  )}
                  <div id={`${id}-help`} className="muted setting-help">
                    {s.description}
                    {h ? ` (${h})` : ''} Default: {s.secret ? '(none)' : describeDefault(s)}.
                    {s.pushToNodes ? ' Pushed to game clients.' : ''}
                    {isAdmin && !s.requiresRestart && overrides.has(s.key) && (
                      <>
                        {' '}
                        <button type="button" className="ghost" disabled={saving} onClick={() => void reset(s)}>
                          Reset to default
                        </button>
                      </>
                    )}
                  </div>
                  {err && (
                    <div className="field-error" role="alert">
                      {err}
                    </div>
                  )}
                </div>
              );
            })}
          </fieldset>
        ))}
        {formError && <p role="alert">{formError}</p>}
        {saved && (
          <p className="notice" role="status">
            {saved}
          </p>
        )}
        {isAdmin ? (
          <div className="form-actions">
            <button type="button" className="ghost" disabled={dirtyKeys.length === 0 || saving} onClick={() => setDraft({})}>
              Discard
            </button>
            <button type="submit" disabled={dirtyKeys.length === 0 || saving}>
              Save changes{dirtyKeys.length > 0 ? ` (${dirtyKeys.length})` : ''}
            </button>
          </div>
        ) : (
          <p className="muted">Viewers can read settings but not change them.</p>
        )}
      </form>
    </section>
  );
}
