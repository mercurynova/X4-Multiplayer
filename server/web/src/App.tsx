import { BrowserRouter } from 'react-router';
import { AppRoutes } from './AppRoutes';
import { AuthProvider } from './auth/AuthContext';

export function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </AuthProvider>
  );
}
