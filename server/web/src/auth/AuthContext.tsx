import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ApiError, http, postJson } from '../api/http';
import type { MeDto } from '../generated/generated';

export interface AuthState {
  /** `loading` until the first `/api/v1/auth/me` answer arrives. */
  status: 'loading' | 'anonymous' | 'authenticated';
  me: MeDto | null;
  isAuthenticated: boolean;
  mustChangePassword: boolean;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  changePassword: (current: string, next: string) => Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

async function fetchMe(): Promise<MeDto | null> {
  try {
    return await http<MeDto>('/api/v1/auth/me');
  } catch (e) {
    if (e instanceof ApiError && (e.status === 401 || e.status === 403)) return null;
    throw e;
  }
}

/**
 * Session state backed by `/api/v1/auth/me`. Pass `initialMe` (a MeDto or null) to skip the initial request,
 * which is what tests do.
 */
export function AuthProvider({ children, initialMe }: { children: ReactNode; initialMe?: MeDto | null }) {
  const [me, setMe] = useState<MeDto | null>(initialMe ?? null);
  const [loading, setLoading] = useState(initialMe === undefined);

  useEffect(() => {
    if (initialMe !== undefined) return;
    let cancelled = false;
    fetchMe()
      .then((m) => {
        if (!cancelled) setMe(m);
      })
      .catch(() => {
        if (!cancelled) setMe(null);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [initialMe]);

  const login = useCallback(async (username: string, password: string) => {
    await postJson('/api/v1/auth/login', { username, password });
    setMe(await fetchMe());
  }, []);

  const logout = useCallback(async () => {
    try {
      await postJson('/api/v1/auth/logout');
    } finally {
      setMe(null);
    }
  }, []);

  const changePassword = useCallback(async (current: string, next: string) => {
    await postJson('/api/v1/auth/change-password', { current, new: next });
    setMe(await fetchMe());
  }, []);

  const value = useMemo<AuthState>(
    () => ({
      status: loading ? 'loading' : me ? 'authenticated' : 'anonymous',
      me,
      isAuthenticated: me !== null,
      mustChangePassword: me?.mustChangePassword ?? false,
      login,
      logout,
      changePassword,
    }),
    [loading, me, login, logout, changePassword],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
