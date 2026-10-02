import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { THEME_STORAGE_KEY, ThemeProvider, ThemeSwitch, useTheme } from './ThemeProvider';

let systemDark: boolean;
let listeners: ((e: MediaQueryListEvent) => void)[];

beforeEach(() => {
  systemDark = false;
  listeners = [];
  window.localStorage.clear();
  delete document.documentElement.dataset.theme;
  vi.stubGlobal(
    'matchMedia',
    vi.fn(() => ({
      get matches() {
        return systemDark;
      },
      addEventListener: (_: string, l: (e: MediaQueryListEvent) => void) => listeners.push(l),
      removeEventListener: (_: string, l: (e: MediaQueryListEvent) => void) => {
        listeners = listeners.filter((x) => x !== l);
      },
    })),
  );
});
afterEach(() => vi.unstubAllGlobals());

function Probe() {
  const { mode, resolved } = useTheme();
  return <p>{`${mode}/${resolved}`}</p>;
}

const mount = () =>
  render(
    <ThemeProvider>
      <ThemeSwitch />
      <Probe />
    </ThemeProvider>,
  );

describe('theme', () => {
  it('follows the system preference by default, including live changes', () => {
    systemDark = true;
    mount();
    expect(screen.getByText('system/dark')).toBeInTheDocument();
    expect(document.documentElement.dataset.theme).toBe('dark');
    act(() => {
      systemDark = false;
      listeners.forEach((l) => l({ matches: false } as MediaQueryListEvent));
    });
    expect(screen.getByText('system/light')).toBeInTheDocument();
    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('persists an explicit choice and restores it on the next load', async () => {
    systemDark = true;
    const first = mount();
    await userEvent.selectOptions(screen.getByLabelText('Theme'), 'light');
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
    first.unmount();

    mount();
    expect(screen.getByText('light/light')).toBeInTheDocument(); // wins over the dark system preference
  });

  it('still works when storage throws', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    mount();
    await userEvent.selectOptions(screen.getByLabelText('Theme'), 'dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
    vi.restoreAllMocks();
  });
});
