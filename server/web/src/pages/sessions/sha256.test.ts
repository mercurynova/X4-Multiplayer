import { describe, expect, it } from 'vitest';
import { Sha256, sha256OfBlob } from './sha256';

async function shaHex(data: Uint8Array): Promise<string> {
  const d = await crypto.subtle.digest('SHA-256', data as BufferSource);
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

const enc = (s: string) => new TextEncoder().encode(s);

describe('Sha256', () => {
  it('matches the known vectors', () => {
    expect(new Sha256().digest()).toBe('e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
    expect(new Sha256().update(enc('abc')).digest()).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
    expect(new Sha256().update(enc('abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq')).digest()).toBe(
      '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1',
    );
  });

  it('is independent of how the input is chunked (including lengths around the 56/64 byte boundaries)', async () => {
    for (const len of [0, 1, 55, 56, 57, 63, 64, 65, 119, 120, 1000]) {
      const data = new Uint8Array(len).map((_, i) => (i * 31 + 7) & 255);
      const want = await shaHex(data);
      const h = new Sha256();
      for (let i = 0; i < len; i += 13) h.update(data.subarray(i, Math.min(i + 13, len)));
      expect(h.digest(), `len ${len}`).toBe(want);
    }
  });

  it('hashes a blob slice by slice', async () => {
    const data = new Uint8Array(100_000).map((_, i) => (i * 7) & 255);
    const progress: number[] = [];
    const hex = await sha256OfBlob(new Blob([data]), (d) => progress.push(d), undefined, 30_000);
    expect(hex).toBe(await shaHex(data));
    expect(progress).toEqual([30_000, 60_000, 90_000, 100_000]);
  });
});
