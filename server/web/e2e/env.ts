import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..', '..');
const exe = process.platform === 'win32' ? '.exe' : '';
const int = (name: string, fallback: number) => Number(process.env[name] ?? fallback);

/**
 * Where everything lives for an e2e run. The ports are not the product defaults (47780/47781/47790) so a developer's
 * own server can keep running; override with X4MP_E2E_* when they clash. This module is imported by the config, the
 * global setup and the specs, so keep it free of side effects.
 */
export const e2e = {
  webPort: int('X4MP_E2E_WEB_PORT', 5273),
  httpPort: int('X4MP_E2E_HTTP_PORT', 47890),
  tcpPort: int('X4MP_E2E_TCP_PORT', 47880),
  udpPort: int('X4MP_E2E_UDP_PORT', 47881),
  /** Fresh on every run: the global setup deletes and recreates it. */
  dataDir: process.env['X4MP_E2E_DATA_DIR'] ?? path.join(os.tmpdir(), 'x4mp-e2e-data'),
  serverExe:
    process.env['X4MP_E2E_SERVER_EXE'] ?? path.join(repoRoot, 'server', 'src', 'X4MP.Server', 'bin', 'Debug', 'net10.0', `x4mp-server${exe}`),
  fakeNodeExe:
    process.env['X4MP_E2E_FAKENODE_EXE'] ?? path.join(repoRoot, 'tools', 'X4MP.FakeNode', 'bin', 'Debug', 'net10.0', `X4MP.FakeNode${exe}`),
  storageState: path.join(here, '.auth', 'admin.json'),
  /** The admin password the global setup sets after the forced change. Only for the throw-away e2e server. */
  adminUser: 'admin',
  adminPassword: 'E2e-only-password-12345',
  get webUrl() {
    return `http://127.0.0.1:${this.webPort}`;
  },
  get httpUrl() {
    return `http://127.0.0.1:${this.httpPort}`;
  },
  get nodeAddress() {
    return `127.0.0.1:${this.tcpPort}`;
  },
};
