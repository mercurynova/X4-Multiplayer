import type { ConnectionState, GroupHandler, HubClient } from '../hub/HubManager';
import type { GroupSpec } from '../hub/contract';

/** A HubClient for page tests that keeps the group handlers, so a test can push group events and the Subscribe* snapshot. */
export class CaptureHub implements HubClient {
  state: ConnectionState = 'connected';
  readonly handlers: { spec: GroupSpec; handler: GroupHandler }[] = [];
  readonly invoked: { method: string; args: unknown[] }[] = [];
  private listeners = new Map<string, Set<(p: unknown) => void>>();

  getState() {
    return this.state;
  }
  subscribeState() {
    return () => undefined;
  }
  on(event: string, handler: (p: unknown) => void) {
    let set = this.listeners.get(event);
    if (!set) this.listeners.set(event, (set = new Set()));
    set.add(handler);
    return () => {
      set.delete(handler);
    };
  }
  acquire(spec: GroupSpec, handler: GroupHandler) {
    const entry = { spec, handler };
    this.handlers.push(entry);
    return () => {
      const i = this.handlers.indexOf(entry);
      if (i >= 0) this.handlers.splice(i, 1);
    };
  }
  invoke(method: string, ...args: unknown[]) {
    this.invoked.push({ method, args });
    return Promise.resolve(undefined);
  }
  /** Group keys currently joined. */
  get keys() {
    return this.handlers.map((h) => h.spec.key);
  }
  /** Deliver a group event to the groups that list it (`$snapshot`/`$error` go to every group, or only the one with `key`). */
  push(event: string, payload: unknown, key?: string) {
    for (const { spec, handler } of [...this.handlers]) {
      if (key && spec.key !== key) continue;
      if (event.startsWith('$') || spec.events.includes(event)) handler(event, payload);
    }
  }
  /** Deliver a push that is not tied to a group (SettingsChanged, Alert). */
  emit(event: string, payload: unknown) {
    this.listeners.get(event)?.forEach((h) => h(payload));
  }
}
