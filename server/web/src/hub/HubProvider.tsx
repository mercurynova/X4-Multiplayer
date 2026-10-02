import { createContext, useContext, useEffect, useMemo, useRef, useSyncExternalStore, type ReactNode } from 'react';
import type { GroupSpec } from './contract';
import { HubManager, type ConnectionState, type GroupHandler, type HubClient } from './HubManager';
import { createSignalRTransport } from './signalrTransport';

const HubContext = createContext<HubClient | null>(null);

export interface HubProviderProps {
  children: ReactNode;
  /** Connect only while true (signed in and past the forced password change). */
  enabled?: boolean;
  /** Inject a client in tests. A HubManager is created for the real connection when omitted. */
  client?: HubClient;
}

/**
 * One SignalR connection to `/hubs/admin` for the whole app. Reconnects with backoff and re-joins every active group.
 */
export function HubProvider({ children, enabled = true, client }: HubProviderProps) {
  const manager = useMemo<HubClient>(() => client ?? new HubManager(() => createSignalRTransport()), [client]);
  useEffect(() => {
    if (client || !enabled) return; // an injected client is driven by the test
    const m = manager as HubManager;
    m.start();
    return () => {
      void m.stop();
    };
  }, [manager, client, enabled]);
  return <HubContext.Provider value={manager}>{children}</HubContext.Provider>;
}

export function useHub(): HubClient {
  const hub = useContext(HubContext);
  if (!hub) throw new Error('useHub must be used inside HubProvider');
  return hub;
}

/** Connection state, re-rendering on change. */
export function useHubState(): ConnectionState {
  const hub = useHub();
  return useSyncExternalStore(
    (cb) => hub.subscribeState(cb),
    () => hub.getState(),
  );
}

/**
 * Joins a hub group while the component is mounted (ref-counted across components, re-joined after a reconnect). The
 * handler gets `(event, payload)`; the Subscribe* call's return value arrives as event `$snapshot`, a failed subscribe as
 * `$error`. The latest handler is always used, so an inline arrow is fine. Pass `null` to skip subscribing.
 */
export function useHubGroup(spec: GroupSpec | null, handler: GroupHandler): void {
  const hub = useHub();
  const handlerRef = useRef(handler);
  useEffect(() => {
    handlerRef.current = handler;
  });
  const key = spec?.key ?? null;
  const specRef = useRef(spec);
  useEffect(() => {
    specRef.current = spec;
  });
  useEffect(() => {
    const s = specRef.current;
    if (!s) return;
    return hub.acquire(s, (event, payload) => handlerRef.current(event, payload));
  }, [hub, key]);
}

/** Listens to a push independent of any group (alerts, settings changes). */
export function useHubEvent(event: string, handler: (payload: unknown) => void): void {
  const hub = useHub();
  const handlerRef = useRef(handler);
  useEffect(() => {
    handlerRef.current = handler;
  });
  useEffect(() => hub.on(event, (p) => handlerRef.current(p)), [hub, event]);
}
