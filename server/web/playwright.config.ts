import { defineConfig } from '@playwright/test';
import { e2e } from './e2e/env';

/**
 * E2E against the real server exe (started by e2e/global-setup.ts on a temp data dir) with the Vite dev server in front
 * of it for the GUI. One worker: the specs share one server and one session, so they run in file order.
 * See e2e/README.md.
 */
export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  use: {
    baseURL: e2e.webUrl,
    storageState: e2e.storageState,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
  webServer: {
    command: `npx vite --host 127.0.0.1 --port ${e2e.webPort} --strictPort`,
    url: e2e.webUrl,
    reuseExistingServer: false,
    timeout: 60_000,
    env: { X4MP_BACKEND: e2e.httpUrl },
  },
});
