import { vi } from 'vitest';
import type { GroupSpec } from '../hub/contract';
import type { ConnectionState, HubClient } from '../hub/HubManager';

/** HubClient for component tests: records acquired groups and lets the test push events. */
export class FakeHubClient implements HubClient {
  state: ConnectionState = 'connected';
  readonly acquired: GroupSpec[] = [];
  private stateListeners = new Set<(s: ConnectionState) => void>();
  private listeners = new Map<string, Set<(p: unknown) => void>>();

  getState() {
    return this.state;
  }
  subscribeState(l: (s: ConnectionState) => void) {
    this.stateListeners.add(l);
    return () => {
      this.stateListeners.delete(l);
    };
  }
  on(event: string, handler: (p: unknown) => void) {
    let set = this.listeners.get(event);
    if (!set) this.listeners.set(event, (set = new Set()));
    set.add(handler);
    return () => {
      set.delete(handler);
    };
  }
  acquire(spec: GroupSpec) {
    this.acquired.push(spec);
    return () => undefined;
  }
  invoke() {
    return Promise.resolve(undefined);
  }
  setState(s: ConnectionState) {
    this.state = s;
    this.stateListeners.forEach((l) => l(s));
  }
  emit(event: string, payload: unknown) {
    this.listeners.get(event)?.forEach((h) => h(payload));
  }
}

export type Call = { url: string; method: string; csrf: string | null; body: string | null };

export const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

/**
 * Stubs global fetch. The header's passive calls (/server, /sessions/current) are answered here so tests only script
 * the calls they care about; every call is recorded.
 */
export function mockApi(handler: (call: Call) => Response) {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init: RequestInit = {}) => {
      const headers = new Headers(init.headers);
      const call: Call = {
        url,
        method: init.method ?? 'GET',
        csrf: headers.get('X-X4MP'),
        body: typeof init.body === 'string' ? init.body : null,
      };
      calls.push(call);
      if (url === '/api/v1/sessions/current') return Promise.resolve(new Response(null, { status: 204 }));
      if (url === '/api/v1/server') return Promise.resolve(json(200, { name: 'Test Server' }));
      return Promise.resolve(handler(call));
    }),
  );
  return calls;
}
