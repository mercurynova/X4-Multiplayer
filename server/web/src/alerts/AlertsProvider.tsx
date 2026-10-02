import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import type { HubAlert } from '../hub/contract';
import { useHubEvent } from '../hub/HubProvider';

export type Severity = 'info' | 'success' | 'warning' | 'error';

export interface Toast {
  id: number;
  severity: Severity;
  text: string;
}

export interface Banner {
  id: string;
  severity: Severity;
  text: string;
}

interface AlertsApi {
  toasts: readonly Toast[];
  banners: readonly Banner[];
  /** Transient message; disappears after `ttlMs` (default 6 s, errors 10 s). */
  toast: (severity: Severity, text: string, ttlMs?: number) => void;
  dismissToast: (id: number) => void;
  /** Persistent banner, replaced if the id exists. */
  showBanner: (id: string, severity: Severity, text: string) => void;
  dismissBanner: (id: string) => void;
}

const AlertsContext = createContext<AlertsApi | null>(null);

function toSeverity(s: string): Severity {
  const v = s.toLowerCase();
  if (v === 'error' || v === 'critical') return 'error';
  if (v === 'warning' || v === 'warn') return 'warning';
  if (v === 'success') return 'success';
  return 'info';
}

/** Toast + banner state, plus the hub `Alert` push. Must sit inside HubProvider. */
export function AlertsProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const [banners, setBanners] = useState<Banner[]>([]);
  const nextId = useRef(1);

  const dismissToast = useCallback((id: number) => setToasts((t) => t.filter((x) => x.id !== id)), []);
  const toast = useCallback(
    (severity: Severity, text: string, ttlMs?: number) => {
      const id = nextId.current++;
      setToasts((t) => [...t.slice(-4), { id, severity, text }]);
      const ttl = ttlMs ?? (severity === 'error' ? 10_000 : 6_000);
      if (ttl > 0) setTimeout(() => dismissToast(id), ttl);
    },
    [dismissToast],
  );
  const showBanner = useCallback(
    (id: string, severity: Severity, text: string) =>
      setBanners((b) => [...b.filter((x) => x.id !== id), { id, severity, text }]),
    [],
  );
  const dismissBanner = useCallback((id: string) => setBanners((b) => b.filter((x) => x.id !== id)), []);

  useHubEvent('Alert', (p) => {
    const a = p as HubAlert;
    if (!a || typeof a.message !== 'string') return;
    const text = a.title ? `${a.title}: ${a.message}` : a.message;
    if (a.persistent) showBanner(a.id ?? a.code ?? text, toSeverity(a.severity), text);
    else toast(toSeverity(a.severity), text);
  });

  const value = useMemo(
    () => ({ toasts, banners, toast, dismissToast, showBanner, dismissBanner }),
    [toasts, banners, toast, dismissToast, showBanner, dismissBanner],
  );
  return <AlertsContext.Provider value={value}>{children}</AlertsContext.Provider>;
}

export function useAlerts(): AlertsApi {
  const ctx = useContext(AlertsContext);
  if (!ctx) throw new Error('useAlerts must be used inside AlertsProvider');
  return ctx;
}

/** Persistent banner slot (under the header). Errors/warnings are announced assertively. */
export function BannerSlot() {
  const { banners, dismissBanner } = useAlerts();
  if (banners.length === 0) return null;
  return (
    <div className="banners">
      {banners.map((b) => (
        <div key={b.id} className={`banner banner-${b.severity}`} role={b.severity === 'error' ? 'alert' : 'status'}>
          <span>
            <strong>{b.severity === 'error' ? 'Error' : b.severity === 'warning' ? 'Warning' : 'Notice'}:</strong> {b.text}
          </span>
          <button type="button" className="ghost" onClick={() => dismissBanner(b.id)} aria-label="Dismiss alert">
            x
          </button>
        </div>
      ))}
    </div>
  );
}

export function ToastRegion() {
  const { toasts, dismissToast } = useAlerts();
  return (
    <div className="toasts" aria-live="polite">
      {toasts.map((t) => (
        <div key={t.id} className={`toast toast-${t.severity}`} role="status">
          <span>{t.text}</span>
          <button type="button" className="ghost" onClick={() => dismissToast(t.id)} aria-label="Dismiss">
            x
          </button>
        </div>
      ))}
    </div>
  );
}
