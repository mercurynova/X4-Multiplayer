import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';

export interface AuthState {
  isAuthenticated: boolean;
  login: () => void;
  logout: () => void;
}

const AuthContext = createContext<AuthState | null>(null);

/** Stub provider: no backend yet (M1-W1 replaces this). */
export function AuthProvider({ children, initial = false }: { children: ReactNode; initial?: boolean }) {
  const [isAuthenticated, setAuthenticated] = useState(initial);
  const value = useMemo<AuthState>(
    () => ({ isAuthenticated, login: () => setAuthenticated(true), logout: () => setAuthenticated(false) }),
    [isAuthenticated],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
