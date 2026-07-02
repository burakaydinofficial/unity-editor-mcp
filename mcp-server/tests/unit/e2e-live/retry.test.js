import { test } from 'node:test';
import assert from 'node:assert/strict';
import { retry } from '../../e2e/live/retry.mjs';

test('retry returns once fn stops throwing', async () => {
  let n = 0;
  const r = await retry(async () => { if (++n < 3) throw new Error('not yet'); return n; },
    { timeoutMs: 1000, intervalMs: 5 });
  assert.equal(r, 3);
});

test('retry throws the last error after timeout', async () => {
  await assert.rejects(
    retry(async () => { throw new Error('always'); }, { timeoutMs: 40, intervalMs: 5 }),
    /always/);
});
