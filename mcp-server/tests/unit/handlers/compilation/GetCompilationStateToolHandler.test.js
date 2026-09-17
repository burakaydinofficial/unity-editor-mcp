import { test } from 'node:test';
import assert from 'node:assert';
import { GetCompilationStateToolHandler } from '../../../../src/handlers/compilation/GetCompilationStateToolHandler.js';

const fast = { waitForIdle: true, pollIntervalMs: 1, timeoutMs: 1000, settleMs: 8 };

test('passthrough (no waitForIdle): single snapshot, no poll metadata', async () => {
  let calls = 0;
  const conn = { isConnected: () => true, connect: async () => {}, sendCommand: async (t, p) => { calls++; return { isCompiling: false, errorCount: 0, echo: p }; } };
  const r = await new GetCompilationStateToolHandler(conn).execute({});
  assert.equal(calls, 1);
  assert.equal(r.isCompiling, false);
  assert.equal(r.waited, undefined); // no poll wrapper on a passthrough
  assert.equal(r.echo.includeMessages, true);
});

test('waitForIdle: waits through busy -> idle and returns the final state', async () => {
  let calls = 0;
  const states = [{ isCompiling: true }, { isCompiling: true }, { isCompiling: false, errorCount: 2 }];
  const conn = { isConnected: () => true, connect: async () => {}, sendCommand: async () => states[Math.min(calls++, states.length - 1)] };
  const r = await new GetCompilationStateToolHandler(conn).execute(fast);
  assert.equal(r.sawCompilation, true);
  assert.equal(r.timedOut, false);
  assert.equal(r.errorCount, 2);
  assert.ok(calls >= 3, `expected >=3 polls, got ${calls}`);
});

test('waitForIdle: a disconnected bridge (reload gap) counts as busy, then resolves on reconnect', async () => {
  let isConnCalls = 0;
  const conn = {
    isConnected: () => ++isConnCalls > 2, // down for the first checks (mid-reload), then up
    connect: async () => {},
    sendCommand: async () => ({ isCompiling: false, isUpdating: false, errorCount: 0 }),
  };
  const r = await new GetCompilationStateToolHandler(conn).execute({ ...fast, settleMs: 50 });
  assert.equal(r.sawCompilation, true, 'the reload gap must register as compilation-in-progress');
  assert.ok(r.reconnectGaps >= 1, `expected reconnectGaps>=1, got ${r.reconnectGaps}`);
  assert.equal(r.timedOut, false);
});

test('waitForIdle: never busy -> after the settle window, reports sawCompilation:false', async () => {
  const conn = { isConnected: () => true, connect: async () => {}, sendCommand: async () => ({ isCompiling: false, isUpdating: false, errorCount: 0 }) };
  const started = Date.now();
  const r = await new GetCompilationStateToolHandler(conn).execute(fast);
  assert.equal(r.sawCompilation, false);
  assert.equal(r.timedOut, false);
  assert.ok(Date.now() - started >= 7, 'must wait out the settle window before concluding no compile');
});

test('waitForIdle: still busy at the deadline -> timedOut:true (no infinite hang)', async () => {
  const conn = { isConnected: () => true, connect: async () => {}, sendCommand: async () => ({ isCompiling: true }) };
  const r = await new GetCompilationStateToolHandler(conn).execute({ ...fast, timeoutMs: 15 });
  assert.equal(r.timedOut, true);
  assert.equal(r.sawCompilation, true);
});

test('validate: negative timing params are rejected', () => {
  const h = new GetCompilationStateToolHandler({});
  assert.throws(() => h.validate({ timeoutMs: -1 }), /non-negative/);
  assert.throws(() => h.validate({ pollIntervalMs: 'soon' }), /non-negative/);
  assert.doesNotThrow(() => h.validate({ timeoutMs: 5000, waitForIdle: true }));
});
