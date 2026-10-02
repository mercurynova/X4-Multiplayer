import { ERROR_EVENT, HUB_EVENTS, SNAPSHOT_EVENT, type GroupSpec } from './contract';

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/** One live connection. The manager creates a fresh one per (re)connect attempt. Implemented over SignalR and by test fakes. */
export interface HubTransport {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
  /** Registers a server push handler; must be called before `start`. */
  on(event: string, handler: (...args: unknown[]) => void): void;
  /** Called once if the connection drops after a successful start. */
  onClose(handler: (error?: Error) => void): void;
}

export type GroupHandler = (event: string, payload: unknown) => void;

/** What pages and providers see (and what tests fake). */
export interface HubClient {
  getState(): ConnectionState;
  subscribeState(listener: (s: ConnectionState) => void): () => void;
  /** Listen to a push regardless of group membership (e.g. `Alert`). */
  on(event: string, handler: (payload: unknown) => void): () => void;
  /** Join a group (ref-counted); returns the release function. */
  acquire(spec: GroupSpec, handler: GroupHandler): () => void;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
}

export interface HubManagerOptions {
  /** Delay before reconnect attempt `n` (0-based), in ms. */
  backoff?: (attempt: number) => number;
}

/** 0.5 s, 1 s, 2 s, ... capped at 15 s, plus up to 20% jitter. */
export function defaultBackoff(attempt: number): number {
  const base = Math.min(15_000, 500 * 2 ** attempt);
  return Math.round(base * (1 + Math.random() * 0.2));
}

interface GroupEntry {
  spec: GroupSpec;
  handlers: Set<GroupHandler>;
  /** True once the Subscribe call succeeded on the CURRENT transport. */
  joined: boolean;
}

/**
 * Owns the single hub connection: connect, reconnect with backoff (a fresh transport per attempt, which is also what
 * SignalR's automatic reconnect does since the server forgets group membership), and the ref-counted group registry.
 * Pure TypeScript with injected timers so it is testable with a fake transport.
 */
export class HubManager implements HubClient {
  private state: ConnectionState = 'disconnected';
  private readonly stateListeners = new Set<(s: ConnectionState) => void>();
  private readonly eventListeners = new Map<string, Set<(payload: unknown) => void>>();
  private readonly groups = new Map<string, GroupEntry>();
  private transport: HubTransport | null = null;
  private running = false;
  private attempt = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private generation = 0;
  private readonly backoff: (attempt: number) => number;

  constructor(
    private readonly factory: () => HubTransport,
    options: HubManagerOptions = {},
  ) {
    this.backoff = options.backoff ?? defaultBackoff;
  }

  getState(): ConnectionState {
    return this.state;
  }

  subscribeState(listener: (s: ConnectionState) => void): () => void {
    this.stateListeners.add(listener);
    return () => {
      this.stateListeners.delete(listener);
    };
  }

  on(event: string, handler: (payload: unknown) => void): () => void {
    let set = this.eventListeners.get(event);
    if (!set) this.eventListeners.set(event, (set = new Set()));
    set.add(handler);
    return () => {
      set.delete(handler);
    };
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.attempt = 0;
    this.connect(false);
  }

  async stop(): Promise<void> {
    this.running = false;
    this.generation++;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    const t = this.transport;
    this.transport = null;
    for (const g of this.groups.values()) g.joined = false;
    this.setState('disconnected');
    if (t) await t.stop().catch(() => undefined);
  }

  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    if (!this.transport || this.state !== 'connected') return Promise.reject(new Error('Hub is not connected.'));
    return this.transport.invoke(method, ...args);
  }

  acquire(spec: GroupSpec, handler: GroupHandler): () => void {
    let entry = this.groups.get(spec.key);
    if (!entry) {
      entry = { spec, handlers: new Set(), joined: false };
      this.groups.set(spec.key, entry);
    }
    entry.handlers.add(handler);
    if (entry.handlers.size === 1) this.join(entry);
    else if (entry.joined) this.replaySnapshot(entry, handler);
    let released = false;
    return () => {
      if (released) return;
      released = true;
      const e = this.groups.get(spec.key);
      if (!e) return;
      e.handlers.delete(handler);
      if (e.handlers.size === 0) {
        this.groups.delete(spec.key);
        if (e.joined && e.spec.unsubscribe && this.transport && this.state === 'connected') {
          this.transport.invoke(e.spec.unsubscribe, ...e.spec.args).catch(() => undefined);
        }
      }
    };
  }

  /** A late joiner of an already-joined group has no snapshot of its own; the cached one is replayed. */
  private snapshots = new Map<string, unknown>();
  private replaySnapshot(entry: GroupEntry, handler: GroupHandler) {
    if (this.snapshots.has(entry.spec.key)) handler(SNAPSHOT_EVENT, this.snapshots.get(entry.spec.key));
  }

  private setState(s: ConnectionState) {
    if (this.state === s) return;
    this.state = s;
    this.stateListeners.forEach((l) => l(s));
  }

  private connect(isRetry: boolean) {
    if (!this.running) return;
    const gen = ++this.generation;
    this.setState(isRetry ? 'reconnecting' : 'connecting');
    const t = this.factory();
    for (const ev of HUB_EVENTS) t.on(ev, (...args) => this.dispatch(gen, ev, args.length <= 1 ? args[0] : args));
    t.onClose(() => {
      if (gen !== this.generation) return;
      this.handleDrop();
    });
    t.start().then(
      () => {
        if (gen !== this.generation || !this.running) {
          void t.stop().catch(() => undefined);
          return;
        }
        this.transport = t;
        this.attempt = 0;
        this.setState('connected');
        for (const g of this.groups.values()) this.join(g);
      },
      () => {
        if (gen !== this.generation) return;
        this.handleDrop();
      },
    );
  }

  private handleDrop() {
    if (this.timer) return; // already scheduled
    this.transport = null;
    for (const g of this.groups.values()) g.joined = false;
    this.snapshots.clear();
    if (!this.running) return;
    this.setState('reconnecting');
    const delay = this.backoff(this.attempt++);
    this.timer = setTimeout(() => {
      this.timer = null;
      this.connect(true);
    }, delay);
  }

  /** The server clears admin sector views when a session ends, so a different session id means re-issue SubscribeSector. */
  private onSessionChanged(id: number | null) {
    const previous = this.lastSessionId;
    this.lastSessionId = id;
    if (previous === null || id === null || previous === id) return;
    for (const g of this.groups.values()) if (g.spec.key.startsWith('sector:')) this.join(g);
  }

  private join(entry: GroupEntry) {
    const t = this.transport;
    if (!t || this.state !== 'connected') return; // joined on (re)connect
    const gen = this.generation;
    t.invoke(entry.spec.subscribe, ...entry.spec.args).then(
      (snapshot) => {
        if (gen !== this.generation || this.groups.get(entry.spec.key) !== entry) return;
        entry.joined = true;
        if (snapshot !== undefined && snapshot !== null) {
          this.snapshots.set(entry.spec.key, snapshot);
          entry.handlers.forEach((h) => h(SNAPSHOT_EVENT, snapshot));
        }
      },
      (err: unknown) => {
        if (gen !== this.generation) return;
        entry.handlers.forEach((h) => h(ERROR_EVENT, err));
      },
    );
  }

  private lastSessionId: number | null = null;

  private dispatch(gen: number, event: string, payload: unknown) {
    if (gen !== this.generation) return;
    if (event === 'SessionChanged') this.onSessionChanged((payload as { id?: number } | null)?.id ?? null);
    this.eventListeners.get(event)?.forEach((h) => h(payload));
    for (const g of this.groups.values()) {
      if (g.spec.events.includes(event)) g.handlers.forEach((h) => h(event, payload));
    }
  }
}
