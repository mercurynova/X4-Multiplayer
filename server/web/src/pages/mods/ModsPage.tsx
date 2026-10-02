import { useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import type { ModCatalogEntryDto, ModEntryDto } from '../../generated/generated';
import { CatalogDialog, EntryDialog } from './EntryDialog';
import { EntriesTable } from './EntriesTable';
import { ImportDialog } from './ImportDialog';
import { CatalogPanel, PlayersPanel, RejectionsPanel } from './Panels';
import { PolicyCard } from './PolicyCard';
import { useMods } from './useMods';
import '../players/players.css';
import './mods.css';

type Form = { kind: 'add'; prefill?: ModCatalogEntryDto } | { kind: 'edit'; entry: ModEntryDto } | { kind: 'catalog'; entry: ModCatalogEntryDto } | { kind: 'import' };

export function ModsPage() {
  const { toast } = useAlerts();
  const { state, error, catalog, rejections, refreshAll } = useMods();
  const [form, setForm] = useState<Form | null>(null);

  if (state === null) {
    return (
      <section className="mods">
        <header className="page-head">
          <h1>Mods</h1>
        </header>
        {error ? <p role="alert">{error}</p> : <p className="muted">Loading mods…</p>}
      </section>
    );
  }

  const { policy, canEdit } = state;
  const done = (message: string) => {
    toast('success', message);
    refreshAll();
  };

  return (
    <section className="mods">
      <header className="page-head">
        <h1>Mods</h1>
        <span className="badge">policy v{policy.version}</span>
        <span className="badge">{policy.enforcement}</span>
        {canEdit && (
          <>
            <button type="button" onClick={() => setForm({ kind: 'import' })}>
              Import from authority
            </button>
            <button type="button" onClick={() => setForm({ kind: 'add' })}>
              + Add mod
            </button>
          </>
        )}
      </header>
      {error && <p role="alert">{error}</p>}
      {!canEdit && (
        <p className="muted" role="status">
          Read-only: only admins and mod editors can change the mod list.
        </p>
      )}

      <PolicyCard policy={policy} canEdit={canEdit} onSaved={refreshAll} />

      <div className="panel" aria-label="Session mod list panel">
        <h2>Session mod list</h2>
        <EntriesTable
          policy={policy}
          canEdit={canEdit}
          playersHidden={state.playersHidden}
          onEdit={(entry) => setForm({ kind: 'edit', entry })}
          onChanged={refreshAll}
        />
        {state.saveRequirementsAvailable && <p className="muted">Mods required by the session save are marked in the list.</p>}
      </div>

      <PlayersPanel players={state.players} policyVersion={policy.version} hidden={state.playersHidden} />
      <RejectionsPanel players={state.players} rejections={rejections} />
      <CatalogPanel catalog={catalog} canEdit={canEdit} onEdit={(entry) => setForm({ kind: 'catalog', entry })} onAdd={(prefill) => setForm({ kind: 'add', prefill })} />

      {form?.kind === 'add' && <EntryDialog prefill={form.prefill} onClose={() => setForm(null)} onSaved={done} />}
      {form?.kind === 'edit' && <EntryDialog entry={form.entry} onClose={() => setForm(null)} onSaved={done} />}
      {form?.kind === 'catalog' && <CatalogDialog entry={form.entry} onClose={() => setForm(null)} onSaved={done} />}
      {form?.kind === 'import' && <ImportDialog policy={policy} onClose={() => setForm(null)} onDone={done} />}
    </section>
  );
}
