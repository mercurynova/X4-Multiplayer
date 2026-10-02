import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { test as base, expect, request, type APIRequestContext } from '@playwright/test';
import { e2e } from './env';

/** A running FakeNode process. `output` is everything it printed so far. */
export class Bot {
  readonly lines: string[] = [];
  private exitCode: number | null = null;
  private readonly exited: Promise<number | null>;

  constructor(
    readonly label: string,
    private readonly child: ChildProcess,
  ) {
    const onData = (chunk: Buffer) => {
      for (const l of chunk.toString('utf8').split(/\r?\n/)) if (l.trim() !== '') this.lines.push(l);
    };
    child.stdout?.on('data', onData);
    child.stderr?.on('data', onData);
    this.exited = new Promise((resolve) =>
      child.once('exit', (code) => {
        this.exitCode = code ?? -1;
        resolve(this.exitCode);
      }),
    );
  }

  get output() {
    return this.lines.join('\n');
  }
  get running() {
    return this.exitCode === null;
  }

  /** Resolves when a printed line matches; rejects with the output so far on timeout or exit. */
  async waitForLine(pattern: RegExp, timeoutMs = 20_000): Promise<string> {
    const end = Date.now() + timeoutMs;
    for (;;) {
      const hit = this.lines.find((l) => pattern.test(l));
      if (hit) return hit;
      if (!this.running || Date.now() > end) {
        throw new Error(`${this.label}: no line matching ${pattern} (${this.running ? 'timed out' : `exited ${this.exitCode}`}). Output:\n${this.output}`);
      }
      await new Promise((r) => setTimeout(r, 100));
    }
  }

  async waitForExit(timeoutMs = 20_000): Promise<number | null> {
    return Promise.race([this.exited, new Promise<null>((r) => setTimeout(() => r(null), timeoutMs))]);
  }

  stop() {
    if (!this.running || this.child.pid === undefined) return;
    if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(this.child.pid), '/T', '/F'], { stdio: 'ignore' });
    else this.child.kill('SIGKILL');
  }
}

export interface BotOptions {
  /** `swarm` starts `clients` bots named `<prefix>01..`; `client` starts one bot called `name`; `authority` the fake authority. */
  command: 'swarm' | 'client' | 'authority';
  name?: string;
  namePrefix?: string;
  clients?: number;
  withAuthority?: boolean;
  /** Seconds before the process exits on its own (always set one, so a crashed test never leaves a bot behind). */
  duration?: number;
  /** Extra raw arguments, e.g. ['--team', 'Red']. */
  args?: string[];
}

export class BotLauncher {
  private readonly bots: Bot[] = [];

  start(o: BotOptions): Bot {
    const args: string[] = [o.command, '--server', e2e.nodeAddress, '--duration', String(o.duration ?? 120)];
    if (o.clients !== undefined) args.push('--clients', String(o.clients));
    if (o.withAuthority) args.push('--with-authority');
    if (o.name) args.push('--name', o.name);
    if (o.namePrefix) args.push('--name-prefix', o.namePrefix);
    args.push(...(o.args ?? []));
    const child = spawn(e2e.fakeNodeExe, args, { stdio: ['ignore', 'pipe', 'pipe'] });
    const bot = new Bot(`${o.command} ${o.name ?? o.namePrefix ?? ''}`.trim(), child);
    this.bots.push(bot);
    return bot;
  }

  stopAll() {
    for (const b of this.bots) b.stop();
  }
}

interface Fixtures {
  /** Starts FakeNode bots against the e2e server; everything is killed when the test ends. */
  bots: BotLauncher;
  /** The admin REST API, signed in (X-X4MP header and cookie set), for arranging state and checking results. */
  api: APIRequestContext;
}

interface WorkerFixtures {
  /**
   * One fake authority for the whole worker: it uploads the first checkpoint, which starts the session, so clients can
   * join. Request it from any test (or `test.beforeEach`) that starts client bots.
   */
  authority: Bot;
}

export const test = base.extend<Fixtures, WorkerFixtures>({
  authority: [
    async ({}, use) => {
      const launcher = new BotLauncher();
      try {
        const bot = launcher.start({ command: 'authority', duration: 1800 });
        await bot.waitForLine(/checkpoint stored/, 30_000);
        await use(bot);
      } finally {
        launcher.stopAll();
      }
    },
    { scope: 'worker' },
  ],
  bots: async ({}, use) => {
    const launcher = new BotLauncher();
    try {
      await use(launcher);
    } finally {
      launcher.stopAll();
    }
  },
  api: async ({}, use) => {
    const ctx = await request.newContext({
      baseURL: e2e.httpUrl,
      storageState: e2e.storageState,
      extraHTTPHeaders: { 'X-X4MP': '1' },
    });
    await use(ctx);
    await ctx.dispose();
  },
});

export { expect };
