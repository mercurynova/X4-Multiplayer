// @vitest-environment node
/**
 * Live check against a real server (skipped unless X4MP_LIVE_URL is set). Uses the very same `uploadSave` module as the page:
 * uploads a big file, is interrupted midway, "reloads" (fresh call, only the remembered key survives) and resumes, then
 * compares the stored SHA-256 on disk with an independent hash.
 *
 *   X4MP_LIVE_URL=http://127.0.0.1:47790 X4MP_LIVE_DATA=<data dir> X4MP_LIVE_FILE=<save.xml.gz> npx vitest run uploader.live
 */
import { createHash } from 'node:crypto';
import { createReadStream, existsSync, openAsBlob, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import type { SaveDto } from '../../generated/generated';
import { uploadSave, type KeyValueStore, type UploadProgress } from './uploader';

const base = process.env.X4MP_LIVE_URL;
const dataDir = process.env.X4MP_LIVE_DATA ?? '';
const filePath = process.env.X4MP_LIVE_FILE ?? '';

class MemStore implements KeyValueStore {
  m = new Map<string, string>();
  getItem = (k: string) => this.m.get(k) ?? null;
  setItem = (k: string, v: string) => void this.m.set(k, v);
  removeItem = (k: string) => void this.m.delete(k);
}

function hashFile(path: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const h = createHash('sha256');
    createReadStream(path).on('data', (c) => h.update(c)).on('end', () => resolve(h.digest('hex'))).on('error', reject);
  });
}

describe.skipIf(!base)('live resumable upload', () => {
  it('uploads, is interrupted, resumes from the server offset and stores the right SHA-256', async () => {
    const realFetch = globalThis.fetch;
    let cookie = '';
    const call = async (url: string, init: RequestInit = {}) => {
      const headers = new Headers(init.headers);
      headers.set('X-X4MP', '1');
      if (cookie) headers.set('Cookie', cookie);
      const res = await realFetch(base + url, { ...init, headers, duplex: 'half' } as RequestInit);
      const set = res.headers.getSetCookie?.() ?? [];
      if (set.length) cookie = set.map((c) => c.split(';')[0]).join('; ');
      return res;
    };
    globalThis.fetch = ((url: string, init?: RequestInit) => call(url, init)) as typeof fetch;
    try {
      // Sign in with the initial password, then do the forced change (the new password stays in a local file).
      const initialFile = join(dataDir, 'initial-admin-password.txt');
      if (existsSync(initialFile)) {
        const initial = readFileSync(initialFile, 'utf8').trim();
        const login = await call('/api/v1/auth/login', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ username: 'admin', password: initial }),
        });
        expect(login.status).toBe(204);
        const next = createHash('sha256').update(String(Math.random())).digest('hex').slice(0, 24) + 'Aa1!';
        const change = await call('/api/v1/auth/change-password', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ current: initial, new: next }),
        });
        expect(change.status).toBe(204);
        writeFileSync(join(dataDir, '..', 'live-admin-password.txt'), next);
      } else {
        const pw = readFileSync(join(dataDir, '..', 'live-admin-password.txt'), 'utf8').trim();
        const login = await call('/api/v1/auth/login', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ username: 'admin', password: pw }),
        });
        expect(login.status).toBe(204);
      }

      const size = statSync(filePath).size;
      const make = async () => new File([await openAsBlob(filePath)], 'live-save.xml.gz', { lastModified: 4242 });
      const store = new MemStore();
      const expectedSha = await hashFile(filePath);

      // Life 1: pause once about half is stored.
      const ctl = new AbortController();
      let stoppedAt = 0;
      await expect(
        uploadSave(await make(), {
          store,
          signal: ctl.signal,
          onProgress: (p: UploadProgress) => {
            if (p.phase === 'uploading' && p.done >= size / 2 && !ctl.signal.aborted) {
              stoppedAt = p.done;
              ctl.abort();
            }
          },
        }),
      ).rejects.toThrow(/abort/i);
      expect(stoppedAt).toBeGreaterThan(0);
      expect(stoppedAt).toBeLessThan(size);
      expect(store.m.size).toBe(1);

      // Life 2 ("page reloaded"): only the remembered key is left.
      const seen: UploadProgress[] = [];
      const saved: SaveDto = await uploadSave(await make(), { store, onProgress: (p) => seen.push(p) });
      const first = seen.find((p) => p.phase === 'uploading');
      console.log(`size ${size}, interrupted at ${stoppedAt}, resumed from ${first?.done}, resumed flag ${first?.resumed}`);
      expect(seen.some((p) => p.phase === 'hashing')).toBe(false);
      expect(first?.resumed).toBe(true);
      expect(first?.done).toBe(stoppedAt);
      expect(saved.sha256).toBe(expectedSha);

      const onDisk = join(dataDir, 'saves', `${expectedSha}.xml.gz`);
      expect(existsSync(onDisk)).toBe(true);
      expect(await hashFile(onDisk)).toBe(expectedSha);
      const listRes = await call('/api/v1/saves');
      const list = (await listRes.json()) as SaveDto[];
      console.log('list', listRes.status, JSON.stringify(list).slice(0, 300));
      expect(list.find((s) => s.sha256 === expectedSha)?.sizeBytes).toBe(size);
    } finally {
      globalThis.fetch = realFetch;
    }
  }, 600_000);
});
