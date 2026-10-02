import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

export type ThemeMode = 'system' | 'light' | 'dark';
export const THEME_STORAGE_KEY = 'x4mp.theme';

interface ThemeState {
  mode: ThemeMode;
  /** What is actually applied: `mode`, or the OS preference when mode is `system`. */
  resolved: 'light' | 'dark';
  setMode: (m: ThemeMode) => void;
}

const ThemeContext = createContext<ThemeState | null>(null);

// localStorage is a per-viewer convenience only: it can be blocked or throw, so the app must work without it.
function readStored(): ThemeMode {
  try {
    const v = window.localStorage.getItem(THEME_STORAGE_KEY);
    if (v === 'light' || v === 'dark' || v === 'system') return v;
  } catch {
    // unavailable
  }
  return 'system';
}

function systemQuery(): MediaQueryList | null {
  return typeof window.matchMedia === 'function' ? window.matchMedia('(prefers-color-scheme: dark)') : null;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [mode, setModeState] = useState<ThemeMode>(readStored);
  const [systemDark, setSystemDark] = useState(() => systemQuery()?.matches ?? false);

  useEffect(() => {
    const mq = systemQuery();
    if (!mq) return;
    const onChange = (e: MediaQueryListEvent) => setSystemDark(e.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);

  const resolved: 'light' | 'dark' = mode === 'system' ? (systemDark ? 'dark' : 'light') : mode;

  useEffect(() => {
    document.documentElement.dataset.theme = resolved;
  }, [resolved]);

  const setMode = useCallback((m: ThemeMode) => {
    setModeState(m);
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, m);
    } catch {
      // unavailable
    }
  }, []);

  const value = useMemo(() => ({ mode, resolved, setMode }), [mode, resolved, setMode]);
  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeState {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error('useTheme must be used inside ThemeProvider');
  return ctx;
}

/** Compact three-way switch for the header. */
export function ThemeSwitch() {
  const { mode, setMode } = useTheme();
  return (
    <label className="theme-switch">
      <span className="visually-hidden">Theme</span>
      <select value={mode} onChange={(e) => setMode(e.target.value as ThemeMode)} aria-label="Theme">
        <option value="system">System</option>
        <option value="light">Light</option>
        <option value="dark">Dark</option>
      </select>
    </label>
  );
}
