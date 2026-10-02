import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { groups, SNAPSHOT_EVENT, ERROR_EVENT } from './contract';
import { HubManager, type HubTransport } from './HubManager';

class FakeTransport implements HubTransport {
  handlers = new Map<string, (...a: unknown[]) => void>();
  closeHandler: (e?: Error) => void = () => undefined;
  invocations: { method: string; args: unknown[] }[] = [];
  startResult: 'ok' | 'fail' = 'ok';
  results = new Map<string, unknown>();
  failMethods = new Set<string>();
  stopped = false;
  start() {
    return this.startResult === 'ok' ? Promise.resolve() : Promise.reject(new Error('refused'));
  }
  stop() {
    this.stopped = true;
    return Promise.resolve();
  }
  invoke(method: string, ...args: unknown[]) {
    this.invocations.push({ method, args });
    if (this.failMethods.has(method)) return Promise.reject(new Error('denied'));
    return Promise.resolve(this.results.get(method));
  }
  on(event: string, handler: (...a: unknown[]) => void) {
    this.handlers.set(event, handler);
  }
  onClose(handler: (e?: Error) => void) {
    this.closeHandler = handler;
  }
  push(event: string, payload: unknown) {
    this.handlers.get(event)?.(payload);
  }
  drop() {
    this.closeHandler(new Error('connection lost'));
  }
  methods() {
    return this.invocations.map((i) => i.method);
  }
}

let transports: FakeTransport[];
let nextStart: 'ok' | 'fail';
const delays: number[] = [];

function makeManager() {
  return new HubManager(
    () => {
      const t = new FakeTransport();
      t.startResult = nextStart;
      transports.push(t);
      return t;
    },
    { backoff: (n) => (delays.push(n), 1000 * (n + 1)) },
  );
}

const T = (i: number) => transports[i]!;
const flush = () => vi.advanceTimersByTimeAsync(0);

beforeEach(() => {
  vi.useFakeTimers();
  transports = [];
  nextStart = 'ok';
  delays.length = 0;
});
afterEach(() => vi.useRealTimers());

describe('HubManager connection', () => {
  it('connects and reports state changes', async () => {
    const m = makeManager();
    const states: string[] = [];
    m.subscribeState((s) => states.push(s));
    m.start();
    expect(m.getState()).toBe('connecting');
    await flush();
    expect(m.getState()).toBe('connected');
    expect(states).toEqual(['connecting', 'connected']);
  });

  it('reconnects with growing backoff after a drop and goes back to connected', async () => {
    const m = makeManager();
    m.start();
    await flush();
    T(0).drop();
    expect(m.getState()).toBe('reconnecting');
    expect(transports).toHaveLength(1);

    nextStart = 'fail'; // the next two attempts are refused
    await vi.advanceTimersByTimeAsync(1000);
    expect(transports).toHaveLength(2);
    expect(m.getState()).toBe('reconnecting');
    await vi.advanceTimersByTimeAsync(1999); // second backoff is 2000
    expect(transports).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(transports).toHaveLength(3);

    nextStart = 'ok';
    await vi.advanceTimersByTimeAsync(3000);
    expect(transports).toHaveLength(4);
    expect(m.getState()).toBe('connected');
    expect(delays).toEqual([0, 1, 2]);

    // a later drop starts the backoff over
    T(3).drop();
    await vi.advanceTimersByTimeAsync(1000);
    expect(transports).toHaveLength(5);
    expect(delays).toEqual([0, 1, 2, 0]);
  });

  it('retries when the very first connect fails', async () => {
    nextStart = 'fail';
    const m = makeManager();
    m.start();
    await flush();
    expect(m.getState()).toBe('reconnecting');
    nextStart = 'ok';
    await vi.advanceTimersByTimeAsync(1000);
    expect(m.getState()).toBe('connected');
  });

  it('does not reconnect after stop()', async () => {
    const m = makeManager();
    m.start();
    await flush();
    await m.stop();
    expect(m.getState()).toBe('disconnected');
    expect(T(0).stopped).toBe(true);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(transports).toHaveLength(1);
  });

  it('ignores pushes from a connection that was replaced', async () => {
    const m = makeManager();
    const got: unknown[] = [];
    m.on('Alert', (p) => got.push(p));
    m.start();
    await flush();
    T(0).drop();
    await vi.advanceTimersByTimeAsync(1000);
    T(0).push('Alert', 'stale');
    T(1).push('Alert', 'fresh');
    expect(got).toEqual(['fresh']);
  });
});

describe('HubManager sector views', () => {
  it('re-issues SubscribeSector when a new session starts', async () => {
    const m = makeManager();
    m.start();
    await flush();
    m.acquire(groups.sector(7), () => undefined);
    m.acquire(groups.dashboard, () => undefined);
    await flush();
    T(0).push('SessionChanged', { id: 1 });
    T(0).push('SessionChanged', { id: 1 }); // same session: nothing
    T(0).push('SessionChanged', { id: 2 });
    await flush();
    expect(T(0).methods().filter((x) => x === 'SubscribeSector')).toHaveLength(2);
    expect(T(0).methods().filter((x) => x === 'SubscribeDashboard')).toHaveLength(1);
  });
});

describe('HubManager groups', () => {
  it('subscribes once for several holders and unsubscribes with the last release', async () => {
    const m = makeManager();
    m.start();
    await flush();
    const r1 = m.acquire(groups.dashboard, () => undefined);
    const r2 = m.acquire(groups.dashboard, () => undefined);
    await flush();
    expect(T(0).methods()).toEqual(['SubscribeDashboard']);
    r1();
    r1(); // releasing twice must not drop the second holder
    await flush();
    expect(T(0).methods()).toEqual(['SubscribeDashboard']);
    r2();
    await flush();
    expect(T(0).methods()).toEqual(['SubscribeDashboard', 'UnsubscribeDashboard']);
  });

  it('delivers the snapshot and only the group events to every holder', async () => {
    const m = makeManager();
    m.start();
    await flush();
    const received: [string, unknown][] = [];
    T(0).results.set('SubscribeDashboard', { players: 3 });
    m.acquire(groups.dashboard, (e, p) => received.push([e, p]));
    await flush();
    T(0).push('Dashboard', { tick: 1 });
    T(0).push('Chat', 'not for dashboard');
    expect(received).toEqual([
      [SNAPSHOT_EVENT, { players: 3 }],
      ['Dashboard', { tick: 1 }],
    ]);
    const late: [string, unknown][] = [];
    m.acquire(groups.dashboard, (e, p) => late.push([e, p]));
    expect(late).toEqual([[SNAPSHOT_EVENT, { players: 3 }]]); // cached snapshot replayed to a late joiner
  });

  it('subscribes a group acquired before the connection is up, once it connects', async () => {
    const m = makeManager();
    m.acquire(groups.sector(42), () => undefined);
    m.start();
    await flush();
    expect(T(0).invocations).toEqual([{ method: 'SubscribeSector', args: [42] }]);
  });

  it('re-subscribes every active group on a new connection after a drop', async () => {
    const m = makeManager();
    m.start();
    await flush();
    m.acquire(groups.dashboard, () => undefined);
    const releaseChat = m.acquire(groups.chat, () => undefined);
    m.acquire(groups.sector(7), () => undefined);
    await flush();
    releaseChat(); // released groups must not come back
    T(0).drop();
    await vi.advanceTimersByTimeAsync(1000);
    expect(m.getState()).toBe('connected');
    expect(T(1).invocations).toEqual([
      { method: 'SubscribeDashboard', args: [] },
      { method: 'SubscribeSector', args: [7] },
    ]);
  });

  it('reports a failed subscribe to the holders instead of throwing', async () => {
    const m = makeManager();
    m.start();
    await flush();
    T(0).failMethods.add('SubscribeDiagnostics');
    const got: string[] = [];
    m.acquire(groups.diagnostics, (e) => got.push(e));
    await flush();
    expect(got).toEqual([ERROR_EVENT]);
  });

  it('passes the filter as the subscribe argument for logs', async () => {
    const m = makeManager();
    m.start();
    await flush();
    m.acquire(groups.logs({ level: 'warn', source: null, q: null }), () => undefined);
    await flush();
    expect(T(0).invocations[0]).toEqual({ method: 'SubscribeLogs', args: [{ level: 'warn', source: null, q: null }] });
  });
});
