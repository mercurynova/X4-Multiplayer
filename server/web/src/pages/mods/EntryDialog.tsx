import { useState, type FormEvent, type ReactNode } from 'react';
import type { ModCatalogEntryDto, ModEntryDto } from '../../generated/generated';
import { problemToFormErrors, type FormErrors } from '../../lib/problem';
import { Dialog } from '../players/Dialog';
import { modsApi, RULES, VERSION_RULES } from './modsApi';

const NO_ERRORS: FormErrors = { fields: {}, all: {}, form: '' };

function Field({ label, error, children, hint }: { label: string; error?: string; children: ReactNode; hint?: string }) {
  return (
    <label>
      {label}
      {children}
      {hint && <span className="muted"> {hint}</span>}
      {error && (
        <span className="field-error" role="alert">
          {error}
        </span>
      )}
    </label>
  );
}

const CLASS_HELP = 'A wrong ClientOnly override can let a sim mod differ between players and desync the session.';

/** Add or edit one entry of the session mod list. Per-field problems from the server show under the field. */
export function EntryDialog({
  entry,
  prefill,
  onClose,
  onSaved,
}: {
  /** Edit this entry; omit to add one. */
  entry?: ModEntryDto;
  /** Add: start from a catalog entry. */
  prefill?: ModCatalogEntryDto;
  onClose: () => void;
  onSaved: (message: string) => void;
}) {
  const [id, setId] = useState(entry?.id ?? prefill?.id ?? '');
  const [name, setName] = useState(entry?.name ?? prefill?.name ?? '');
  const [rule, setRule] = useState(entry?.rule ?? 'Allowed');
  const [enabled, setEnabled] = useState(entry?.enabled ?? true);
  const [classOverride, setClassOverride] = useState(entry?.classOverride ?? prefill?.classOverride ?? 'Unknown');
  const [versionRule, setVersionRule] = useState(entry?.versionRule ?? 'Any');
  const [version, setVersion] = useState(entry?.version ?? '');
  const [nexusUrl, setNexusUrl] = useState(entry?.nexusUrl ?? prefill?.nexusUrl ?? '');
  const [workshopId, setWorkshopId] = useState(entry?.workshopId ? String(entry.workshopId) : prefill?.workshopId ? String(prefill.workshopId) : '');
  const [notes, setNotes] = useState(entry?.notes ?? prefill?.notes ?? '');
  const [errors, setErrors] = useState<FormErrors>(NO_ERRORS);
  const [busy, setBusy] = useState(false);
  const f = errors.fields;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const ws = workshopId.trim();
    if (ws !== '' && !/^\d+$/.test(ws)) {
      setErrors({ fields: { workshopId: 'Use a Steam Workshop item id (digits only), or leave it empty.' }, all: {}, form: '' });
      return;
    }
    setBusy(true);
    setErrors(NO_ERRORS);
    modsApi
      .putEntry(id.trim(), {
        name: name.trim(),
        rule,
        enabled,
        classOverride,
        versionRule,
        version,
        contentHash: null,
        nexusUrl: nexusUrl.trim(),
        workshopId: ws === '' ? null : Number(ws),
        notes,
      })
      .then(() => {
        onSaved(entry ? `Saved ${name.trim() || id}.` : `Added ${name.trim() || id}.`);
        onClose();
      })
      .catch((err: unknown) => setErrors(problemToFormErrors(err)))
      .finally(() => setBusy(false));
  };

  return (
    <Dialog title={entry ? `Edit ${entry.name}` : 'Add mod'} onClose={onClose}>
      <form onSubmit={submit} noValidate>
        <Field label="Extension id" error={f['extId']}>
          <input value={id} onChange={(e) => setId(e.target.value)} readOnly={!!entry} required autoFocus={!entry} aria-invalid={f['extId'] ? true : undefined} />
        </Field>
        <Field label="Name" error={f['name']}>
          <input value={name} onChange={(e) => setName(e.target.value)} aria-invalid={f['name'] ? true : undefined} />
        </Field>
        <div className="row">
          <Field label="Rule" error={f['rule']}>
            <select value={rule} onChange={(e) => setRule(e.target.value)}>
              {RULES.map((r) => (
                <option key={r}>{r}</option>
              ))}
            </select>
          </Field>
          <label className="check">
            <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Enabled
          </label>
        </div>
        <Field label="Class override" error={f['classOverride']} hint={CLASS_HELP}>
          <select value={classOverride} onChange={(e) => setClassOverride(e.target.value)}>
            <option value="Unknown">No override</option>
            <option value="Sim">Sim</option>
            <option value="ClientOnly">ClientOnly</option>
          </select>
        </Field>
        <div className="row">
          <Field label="Version rule" error={f['versionRule']}>
            <select value={versionRule} onChange={(e) => setVersionRule(e.target.value)}>
              {VERSION_RULES.map((r) => (
                <option key={r}>{r}</option>
              ))}
            </select>
          </Field>
          <Field label="Version" error={f['version']}>
            <input value={version} onChange={(e) => setVersion(e.target.value)} aria-invalid={f['version'] ? true : undefined} />
          </Field>
        </div>
        <Field label="Nexus URL" error={f['nexusUrl']}>
          <input
            value={nexusUrl}
            placeholder="https://www.nexusmods.com/x4foundations/mods/1234"
            onChange={(e) => setNexusUrl(e.target.value)}
            aria-invalid={f['nexusUrl'] ? true : undefined}
          />
        </Field>
        <Field label="Workshop id" error={f['workshopId']}>
          <input value={workshopId} inputMode="numeric" onChange={(e) => setWorkshopId(e.target.value)} aria-invalid={f['workshopId'] ? true : undefined} />
        </Field>
        <Field label="Notes" error={f['notes']}>
          <textarea rows={2} value={notes} maxLength={500} onChange={(e) => setNotes(e.target.value)} aria-invalid={f['notes'] ? true : undefined} />
        </Field>
        {(errors.form || f['body']) && <p role="alert">{f['body'] ?? errors.form}</p>}
        <div className="dialog-buttons">
          <button type="button" className="ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" disabled={busy}>
            {entry ? 'Save changes' : 'Add mod'}
          </button>
        </div>
      </form>
    </Dialog>
  );
}

/** Edit what the server remembers about a mod (name, links, class override, notes) for every session. */
export function CatalogDialog({ entry, onClose, onSaved }: { entry: ModCatalogEntryDto; onClose: () => void; onSaved: (message: string) => void }) {
  const [name, setName] = useState(entry.name);
  const [nexusUrl, setNexusUrl] = useState(entry.nexusUrl ?? '');
  const [workshopId, setWorkshopId] = useState(entry.workshopId ? String(entry.workshopId) : '');
  const [classOverride, setClassOverride] = useState(entry.classOverride);
  const [notes, setNotes] = useState(entry.notes ?? '');
  const [errors, setErrors] = useState<FormErrors>(NO_ERRORS);
  const [busy, setBusy] = useState(false);
  const f = errors.fields;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const ws = workshopId.trim();
    if (ws !== '' && !/^\d+$/.test(ws)) {
      setErrors({ fields: { workshopId: 'Use a Steam Workshop item id (digits only), or leave it empty.' }, all: {}, form: '' });
      return;
    }
    setBusy(true);
    setErrors(NO_ERRORS);
    modsApi
      .putCatalog(entry.id, { name: name.trim(), nexusUrl: nexusUrl.trim(), workshopId: ws === '' ? null : Number(ws), classOverride, notes })
      .then(() => {
        onSaved(`Saved ${name.trim() || entry.id}.`);
        onClose();
      })
      .catch((err: unknown) => setErrors(problemToFormErrors(err)))
      .finally(() => setBusy(false));
  };

  return (
    <Dialog title={`Edit ${entry.name || entry.id} in the catalog`} onClose={onClose}>
      <form onSubmit={submit} noValidate>
        <Field label="Name" error={f['name']}>
          <input value={name} onChange={(e) => setName(e.target.value)} autoFocus />
        </Field>
        <Field label="Nexus URL" error={f['nexusUrl']}>
          <input value={nexusUrl} onChange={(e) => setNexusUrl(e.target.value)} aria-invalid={f['nexusUrl'] ? true : undefined} />
        </Field>
        <Field label="Workshop id" error={f['workshopId']}>
          <input value={workshopId} inputMode="numeric" onChange={(e) => setWorkshopId(e.target.value)} aria-invalid={f['workshopId'] ? true : undefined} />
        </Field>
        <Field label="Class override" error={f['classOverride']} hint={CLASS_HELP}>
          <select value={classOverride} onChange={(e) => setClassOverride(e.target.value)}>
            <option value="Unknown">No override</option>
            <option value="Sim">Sim</option>
            <option value="ClientOnly">ClientOnly</option>
          </select>
        </Field>
        <Field label="Notes" error={f['notes']}>
          <textarea rows={2} value={notes} maxLength={500} onChange={(e) => setNotes(e.target.value)} />
        </Field>
        {(errors.form || f['body']) && <p role="alert">{f['body'] ?? errors.form}</p>}
        <div className="dialog-buttons">
          <button type="button" className="ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" disabled={busy}>
            Save catalog entry
          </button>
        </div>
      </form>
    </Dialog>
  );
}
