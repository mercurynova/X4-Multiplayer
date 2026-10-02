import { act, render } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { groups } from './contract';
import { HubManager, type HubTransport } from './HubManager';
import { HubProvider, useHubGroup } from './HubProvider';

function setup() {
  const calls: string[] = [];
  const handlers = new Map<string, (...a: unknown[]) => void>();
  const transport: HubTransport = {
    start: () => Promise.resolve(),
    stop: () => Promise.resolve(),
    invoke: (m) => {
      calls.push(m);
      return Promise.resolve(undefined);
    },
    on: (e, h) => void handlers.set(e, h),
    onClose: () => undefined,
  };
  const manager = new HubManager(() => transport);
  manager.start();
  return { manager, calls, handlers };
}

function Page({ onEvent }: { onEvent: (e: string, p: unknown) => void }) {
  useHubGroup(groups.dashboard, (e, p) => onEvent(e, p));
  return null;
}

function Two({ onEvent }: { onEvent: (e: string, p: unknown) => void }) {
  const [second, setSecond] = useState(true);
  return (
    <>
      <Page onEvent={onEvent} />
      {second && <Page onEvent={onEvent} />}
      <button onClick={() => setSecond(false)}>drop</button>
    </>
  );
}

describe('useHubGroup', () => {
  it('shares one server subscription between components and releases it on the last unmount', async () => {
    const { manager, calls, handlers } = setup();
    const seen: string[] = [];
    const view = render(
      <HubProvider client={manager}>
        <Two onEvent={(e) => seen.push(e)} />
      </HubProvider>,
    );
    await act(async () => {
      await Promise.resolve();
    });
    expect(calls).toEqual(['SubscribeDashboard']);

    act(() => handlers.get('Dashboard')?.({ tick: 1 }));
    expect(seen.filter((e) => e === 'Dashboard')).toHaveLength(2); // both mounted components got it

    act(() => view.getByText('drop').click());
    expect(calls).toEqual(['SubscribeDashboard']); // still held by the first component
    view.unmount();
    await act(async () => {
      await Promise.resolve();
    });
    expect(calls).toEqual(['SubscribeDashboard', 'UnsubscribeDashboard']);
  });
});
