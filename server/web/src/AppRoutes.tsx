import type { ReactElement } from 'react';
import { Route, Routes } from 'react-router';
import { Layout } from './components/Layout';
import { RequireAuth } from './components/RequireAuth';
import { ChangePassword } from './pages/ChangePassword';
import { Login } from './pages/Login';
import { NotFound } from './pages/NotFound';
import { Placeholder } from './pages/Placeholder';
import { ChatPage } from './pages/chat/ChatPage';
import { DiagnosticsPage } from './pages/diagnostics/DiagnosticsPage';
import { LogsPage } from './pages/logs/LogsPage';
import { SettingsPage } from './pages/settings/SettingsPage';
import { screens } from './screens';

// Screens that have a real page; the rest still render a placeholder.
const pages: Record<string, ReactElement> = {
  '/chat': <ChatPage />,
  '/logs': <LogsPage />,
  '/settings': <SettingsPage />,
  '/diagnostics': <DiagnosticsPage />,
};

// Detail routes from server-design 5.1; the pages themselves arrive with W2-W6 and the M1-T/E tasks.
const detailRoutes = [
  { path: '/players/:id', title: 'Player' },
  { path: '/economy/:tab', title: 'Economy' },
  { path: '/map/:sectorId', title: 'Sector map' },
];

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route element={<RequireAuth />}>
        <Route element={<Layout />}>
          {screens.map((s) => (
            <Route key={s.path} path={s.path} element={pages[s.path] ?? <Placeholder title={s.title} />} />
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
