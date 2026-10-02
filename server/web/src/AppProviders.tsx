import type { ReactNode } from 'react';
import { AlertsProvider } from './alerts/AlertsProvider';
import { AuthProvider, useAuth } from './auth/AuthContext';
import type { MeDto } from './generated/generated';
import type { HubClient } from './hub/HubManager';
import { HubProvider } from './hub/HubProvider';
import { ThemeProvider } from './theme/ThemeProvider';

function AuthedHub({ client, children }: { client?: HubClient; children: ReactNode }) {
  const { isAuthenticated, mustChangePassword } = useAuth();
  // The hub needs a session, and nothing but the password change is allowed before that is done.
  return (
    <HubProvider client={client} enabled={isAuthenticated && !mustChangePassword}>
      <AlertsProvider>{children}</AlertsProvider>
    </HubProvider>
  );
}

/** Everything the pages sit inside, except the router. `hub` and `initialMe` exist for tests. */
export function AppProviders({ children, hub, initialMe }: { children: ReactNode; hub?: HubClient; initialMe?: MeDto | null }) {
  return (
    <ThemeProvider>
      <AuthProvider initialMe={initialMe}>
        <AuthedHub client={hub}>{children}</AuthedHub>
      </AuthProvider>
    </ThemeProvider>
  );
}
