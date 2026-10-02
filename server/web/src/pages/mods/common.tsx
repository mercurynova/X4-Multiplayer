import type { ReactNode } from 'react';
import type { ModRefDto, ModViolationDto } from '../../generated/generated';

const isWeb = (u: string | null | undefined): u is string => !!u && /^https?:\/\//i.test(u);
const isSteam = (u: string | null | undefined): u is string => !!u && /^steam:\/\//i.test(u);

export interface LinkSource {
  nexusUrl: string | null;
  workshopUrl: string | null;
  workshopSteamUrl: string | null;
}

/**
 * Nexus and Workshop links of a mod. They open in a new tab (X4MP never hosts or installs mods, ADR-044); the
 * `steam://` link opens the Steam client straight on the item.
 */
export function ModLinks({ mod }: { mod: LinkSource }) {
  const parts: ReactNode[] = [];
  if (isWeb(mod.nexusUrl))
    parts.push(
      <a key="nexus" href={mod.nexusUrl} target="_blank" rel="noopener noreferrer">
        Nexus
      </a>,
    );
  if (isWeb(mod.workshopUrl))
    parts.push(
      <a key="ws" href={mod.workshopUrl} target="_blank" rel="noopener noreferrer">
        Workshop
      </a>,
    );
  if (isSteam(mod.workshopSteamUrl))
    parts.push(
      <a key="steam" href={mod.workshopSteamUrl} target="_blank" rel="noopener noreferrer" title="Open in the Steam client">
        Steam
      </a>,
    );
  if (parts.length === 0) return <span className="muted">no link</span>;
  return (
    <span className="mod-links">
      {parts.map((p, i) => (
        <span key={i}>
          {i > 0 && ' · '}
          {p}
        </span>
      ))}
    </span>
  );
}

const SECTIONS: readonly { key: keyof Pick<ModViolationDto, 'install' | 'enable' | 'disable' | 'update'>; label: string }[] = [
  { key: 'install', label: 'Install' },
  { key: 'enable', label: 'Enable' },
  { key: 'disable', label: 'Disable' },
  { key: 'update', label: 'Update' },
];

export const violationCount = (v: ModViolationDto | null) => (v ? v.install.length + v.enable.length + v.disable.length + v.update.length : 0);

/** One-line summary: "install Warehouse Fleets · disable X". */
export function violationSummary(v: ModViolationDto | null): string {
  if (!v) return '';
  return SECTIONS.flatMap((s) => v[s.key].map((m) => `${s.label.toLowerCase()} ${m.name || m.id}`)).join(' · ');
}

function Ref({ mod, kind }: { mod: ModRefDto; kind: string }) {
  return (
    <li>
      <strong>{mod.name || mod.id}</strong> <span className="mono">{mod.id}</span>
      {kind === 'update' ? (
        <span>
          {' '}
          have {mod.haveVersion || 'none'}, need {mod.version || 'the session version'}
        </span>
      ) : mod.version ? (
        <span> version {mod.version}</span>
      ) : null}{' '}
      <ModLinks mod={mod} />
      {mod.notes && <span className="muted"> ({mod.notes})</span>}
    </li>
  );
}

/** The exact install / enable / disable / update lists of a violation, each mod with its links. */
export function ViolationLists({ violation }: { violation: ModViolationDto | null }) {
  if (!violation || violationCount(violation) === 0) return <p className="muted">No differences.</p>;
  return (
    <div className="violation">
      {SECTIONS.filter((s) => violation[s.key].length > 0).map((s) => (
        <section key={s.key} aria-label={`${s.label} list`}>
          <h4>{s.label}</h4>
          <ul>
            {violation[s.key].map((m) => (
              <Ref key={m.id} mod={m} kind={s.key} />
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}

export function OutcomeBadge({ outcome }: { outcome: string }) {
  const cls = outcome === 'Rejected' ? 'badge-bad' : outcome === 'Warned' ? 'badge-warn' : outcome === 'Admitted' ? 'badge-ok' : '';
  return <span className={`badge ${cls}`}>{outcome === 'None' ? 'No report' : outcome}</span>;
}

export const formatTime = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '');
