import { test } from 'node:test';
import assert from 'node:assert';
import { GetTestResultsToolHandler } from '../../../../src/handlers/testing/GetTestResultsToolHandler.js';

const fast = { waitForCompletion: true, pollIntervalMs: 1, timeoutMs: 1000 };

test('passthrough (no waitForCompletion): single snapshot, no poll metadata', async () => {
  let calls = 0;
  const conn = { isConnected: () => true, connect: async () => {}, sendCommand: async (t, p) => { calls++; return { isRunning: false, hasResults: true, echo: p }; } };
  const r = await new GetTestResultsToolHandler(conn).execute({});
  assert.equal(calls, 1);
  assert.equal(r.isRunning, false);
  assert.equal(r.waited, undefined);
  assert.equal(r.echo.includeDetails, true);
});

test('waitForCompletion: polls while isRunning, returns the final results when it finishes', async () => {
  let calls = 0;
  const states = [{ isRunning: true }, { isRunning: true }, { isRunning: false, summary: { total: 3, passed: 3, failed: 0 } }];
  const conn = { isConnected: () => true, connect: async () => {}, sendCommand: async () => states[Math.min(calls++, states.length - 1)] };
  const r = await new GetTestResultsToolHandler(conn).execute(fast);
  assert.equal(r.isRunning, false);
  assert.equal(r.timedOut, false);
  assert.equal(r.summary.passed, 3);
  assert.ok(calls >= 3, `expected >=3 polls, got ${calls}`);
});

test('waitForCompletion: tolerates a disconnected bridge (reload) as "still running"', async () => {
  let isConnCalls = 0;
  const conn = {
    isConnected: () => ++isConnCalls > 2, // down for the first checks (mid-reload), then up
    connect: async () => {},
    sendCommand: async () => ({ isRunning: false, summary: { total: 1, passed: 1 } }),
  };
  const r = await new GetTestResultsToolHandler(conn).execute(fast);
  assert.equal(r.isRunning, false);
  assert.ok(r.reconnectGaps >= 1, `expected reconnectGaps>=1, got ${r.reconnectGaps}`);
  assert.equal(r.timedOut, false);
});

test('waitForCompletion: still running at the deadline -> timedOut (no infinite hang)', async () => {
  const conn = { isConnected: () => true, connect: async () => {}, sendCommand: async () => ({ isRunning: true }) };
  const r = await new GetTestResultsToolHandler(conn).execute({ ...fast, timeoutMs: 15 });
  assert.equal(r.timedOut, true);
});

test('validate: negative timing params are rejected', () => {
  const h = new GetTestResultsToolHandler({});
  assert.throws(() => h.validate({ timeoutMs: -1 }), /non-negative/);
  assert.throws(() => h.validate({ pollIntervalMs: 'soon' }), /non-negative/);
  assert.doesNotThrow(() => h.validate({ timeoutMs: 5000, waitForCompletion: true }));
});
