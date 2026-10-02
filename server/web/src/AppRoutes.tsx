import { Route, Routes } from 'react-router';
import { Layout } from './components/Layout';
import { RequireAuth } from './components/RequireAuth';
import { ChangePassword } from './pages/ChangePassword';
import { ChatPage } from './pages/chat/ChatPage';
import { Dashboard } from './pages/dashboard/Dashboard';
import { DiagnosticsPage } from './pages/diagnostics/DiagnosticsPage';
import { LogsPage } from './pages/logs/LogsPage';
import { Login } from './pages/Login';
import { NotFound } from './pages/NotFound';
import { Placeholder } from './pages/Placeholder';
import { PlayerDetailPage } from './pages/players/PlayerDetailPage';
import { PlayersPage } from './pages/players/PlayersPage';
import { SessionsPage } from './pages/sessions/SessionsPage';
import { SettingsPage } from './pages/settings/SettingsPage';
import { TeamsPage } from './pages/teams/TeamsPage';
import { screens } from './screens';

// Screens with a real page; the rest render a placeholder until their task lands.
const implementedScreens = new Set(['/', '/players', '/sessions', '/chat', '/logs', '/settings', '/diagnostics', '/teams']);

// Detail routes from server-design 5.1; the pages themselves arrive with W2-W6 and the M1-T/E tasks.
const detailRoutes = [
  { path: '/economy/:tab', title: 'Economy' },
  { path: '/map/:sectorId', title: 'Sector map' },
];

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route element={<RequireAuth />}>
        <Route element={<Layout />}>
          <Route path="/" element={<Dashboard />} />
          <Route path="/players" element={<PlayersPage />} />
          <Route path="/players/:id" element={<PlayerDetailPage />} />
          <Route path="/sessions" element={<SessionsPage />} />
          <Route path="/chat" element={<ChatPage />} />
          <Route path="/logs" element={<LogsPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/diagnostics" element={<DiagnosticsPage />} />
          <Route path="/teams" element={<TeamsPage />} />
          {screens
            .filter((s) => !implementedScreens.has(s.path))
            .map((s) => (
              <Route key={s.path} path={s.path} element={<Placeholder title={s.title} />} />
            ))}
          {detailRoutes.map((r) => (
            <Route key={r.path} path={r.path} element={<Placeholder title={r.title} />} />
          ))}
          <Route path="/account/password" element={<ChangePassword />} />
          <Route path="*" element={<NotFound />} />
        </Route>
      </Route>
    </Routes>
  );
}
