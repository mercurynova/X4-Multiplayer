import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { AppRoutes } from './AppRoutes';
import { AuthProvider } from './auth/AuthContext';
import { screens } from './screens';

function renderAt(path: string, authenticated: boolean) {
  return render(
    <AuthProvider initial={authenticated}>
      <MemoryRouter initialEntries={[path]}>
        <AppRoutes />
      </MemoryRouter>
    </AuthProvider>,
  );
}

describe('routing', () => {
  it('renders the login page when unauthenticated', () => {
    renderAt('/players', false);
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('shows every screen in the nav when authenticated', () => {
    renderAt('/', true);
    const nav = screen.getByRole('navigation', { name: 'Main' });
    for (const s of screens) {
      expect(within(nav).getByRole('link', { name: s.title })).toBeInTheDocument();
    }
    expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeInTheDocument();
  });

  it('renders the 404 page for unknown routes', () => {
    renderAt('/nope', true);
    expect(screen.getByRole('heading', { name: 'Page not found' })).toBeInTheDocument();
  });
});
