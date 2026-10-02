import { Route, Routes } from 'react-router';
import { Layout } from './components/Layout';
import { RequireAuth } from './components/RequireAuth';
import { ChangePassword } from './pages/ChangePassword';
import { Login } from './pages/Login';
import { NotFound } from './pages/NotFound';
import { Placeholder } from './pages/Placeholder';
import { PlayerDetailPage } from './pages/players/PlayerDetailPage';
import { PlayersPage } from './pages/players/PlayersPage';
import { screens } from './screens';

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
          <Route path="/players" element={<PlayersPage />} />
          <Route path="/players/:id" element={<PlayerDetailPage />} />
          {screens
            .filter((s) => s.path !== '/players')
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
