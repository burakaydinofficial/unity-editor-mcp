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

test('retry attempts fn at least once even with an already-expired deadline', async () => {
  let n = 0;
  assert.equal(await retry(async () => { n++; return 42; }, { timeoutMs: 0, intervalMs: 5 }), 42);
  assert.equal(n, 1); // ran despite timeoutMs:0
});

test('retry fires onRetry once per failure with the error, and NOT on immediate success', async () => {
  const seen = [];
  await retry(async () => { if (seen.length < 2) throw new Error('e' + seen.length); return 'ok'; },
    { timeoutMs: 1000, intervalMs: 1, onRetry: e => seen.push(e.message) });
  assert.deepEqual(seen, ['e0', 'e1']);
  let calls = 0;
  await retry(async () => 'immediate', { timeoutMs: 1000, intervalMs: 1, onRetry: () => calls++ });
  assert.equal(calls, 0);
});

test('retry surfaces a non-Error throw verbatim (no rewrapping)', async () => {
  await assert.rejects(
    retry(async () => { throw 'string-failure'; }, { timeoutMs: 20, intervalMs: 5 }),
    err => err === 'string-failure');
});
