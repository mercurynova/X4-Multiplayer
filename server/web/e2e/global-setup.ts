import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { request } from '@playwright/test';
import { e2e } from './env';

async function waitFor<T>(what: string, probe: () => Promise<T | null | undefined | false>, timeoutMs = 30_000): Promise<T> {
  const end = Date.now() + timeoutMs;
  for (;;) {
    try {
      const v = await probe();
      if (v) return v;
    } catch {
      // not yet
    }
    if (Date.now() > end) throw new Error(`Timed out waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 250));
  }
}

function killTree(child: ChildProcess) {
  if (child.pid === undefined || child.exitCode !== null) return;
  if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
  else child.kill('SIGKILL');
}

/**
 * Starts the real server exe on a fresh temp data dir, then does the bootstrap admin's forced password change over the
 * API and saves the signed-in cookie as the default storage state. Specs therefore start signed in; the login page
 * itself is covered by its own spec with an empty storage state. The returned function stops the server.
 */
export default async function globalSetup() {
  if (!fs.existsSync(e2e.serverExe)) {
    throw new Error(`Server exe not found at ${e2e.serverExe}. Run \`dotnet build X4MP.sln\` first (or set X4MP_E2E_SERVER_EXE).`);
  }
  if (!fs.existsSync(e2e.fakeNodeExe)) {
    throw new Error(`FakeNode exe not found at ${e2e.fakeNodeExe}. Run \`dotnet build X4MP.sln\` first (or set X4MP_E2E_FAKENODE_EXE).`);
  }
  fs.rmSync(e2e.dataDir, { recursive: true, force: true });
  fs.mkdirSync(e2e.dataDir, { recursive: true });
  fs.mkdirSync(path.dirname(e2e.storageState), { recursive: true });

  const logPath = path.join(e2e.dataDir, 'server-stdout.log');
  const log = fs.openSync(logPath, 'w');
  const server = spawn(e2e.serverExe, ['--data-dir', e2e.dataDir, '--port', String(e2e.httpPort)], {
    stdio: ['ignore', log, log],
    env: {
      ...process.env,
      // A swarm from one machine needs more than the default 4 connections per IP.
      X4MP__Net__MaxConnectionsPerIp: '64',
      X4MP__Net__NodeTcpEndpoint: `0.0.0.0:${e2e.tcpPort}`,
      X4MP__Net__UdpPort: String(e2e.udpPort),
    },
  });
  const teardown = () => {
    killTree(server);
    fs.closeSync(log);
  };

  try {
    await waitFor('the server to answer /healthz', async () => {
      if (server.exitCode !== null) throw new Error(`The server exited with ${server.exitCode}; see ${logPath}`);
      return (await fetch(`${e2e.httpUrl}/healthz`)).ok;
    });

    const passwordFile = path.join(e2e.dataDir, 'initial-admin-password.txt');
    const initial = (await waitFor('initial-admin-password.txt', async () => fs.existsSync(passwordFile) && fs.readFileSync(passwordFile, 'utf8').trim())).trim();

    const ctx = await request.newContext({ baseURL: e2e.httpUrl, extraHTTPHeaders: { 'X-X4MP': '1' } });
    const login = async (password: string) => ctx.post('/api/v1/auth/login', { data: { username: e2e.adminUser, password } });
    let res = await login(initial);
    if (!res.ok()) throw new Error(`Bootstrap login failed: ${res.status()}`);
    res = await ctx.post('/api/v1/auth/change-password', { data: { current: initial, new: e2e.adminPassword } });
    if (!res.ok()) throw new Error(`Forced password change failed: ${res.status()} ${await res.text()}`);
    // The change invalidates the session cookie; sign in again with the new password.
    res = await login(e2e.adminPassword);
    if (!res.ok()) throw new Error(`Login after the password change failed: ${res.status()}`);
    await ctx.storageState({ path: e2e.storageState });
    await ctx.dispose();
  } catch (err) {
    teardown();
    throw err;
  }
  return teardown;
}
