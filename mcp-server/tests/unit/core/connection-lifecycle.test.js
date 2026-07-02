// Regression tests for two connection state-machine faults found in the pre-0.21.0 bug hunt (previously uncovered):
//  - Node-3: a failed reconnect must re-schedule (the backoff loop must not die after one attempt).
//  - Node-4: disconnect() during an in-flight connect must settle the awaiter, not wedge it forever.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'events';
import net from 'net';
import { UnityConnection } from '../../../src/core/unityConnection.js';

// UnityConnection._doConnect short-circuits when NODE_ENV=test/CI; unset them for these lifecycle tests.
function withLiveEnv(fn) {
  const env = process.env.NODE_ENV, ci = process.env.CI, dar = process.env.DISABLE_AUTO_RECONNECT;
  delete process.env.NODE_ENV; delete process.env.CI; delete process.env.DISABLE_AUTO_RECONNECT;
  try { return fn(); } finally {
    if (env !== undefined) process.env.NODE_ENV = env;
    if (ci !== undefined) process.env.CI = ci;
    if (dar !== undefined) process.env.DISABLE_AUTO_RECONNECT = dar;
  }
}

test('Node-4: disconnect() during an in-flight connect rejects the awaiter instead of hanging', async () => {
  await withLiveEnv(async () => {
    const orig = net.Socket;
    try {
      const mock = new EventEmitter();
      mock.connect = () => {};      // never emits 'connect'/'error' -> stays in-flight
      mock.destroy = () => {};
      mock.setKeepAlive = () => {};
      net.Socket = function () { return mock; };

      const conn = new UnityConnection({ host: '127.0.0.1', port: 61999 });
      const p = conn.connect();     // pending (the mock never settles on its own)
      conn.disconnect();            // must abort the in-flight connect
      await assert.rejects(p, /Disconnected during connect/);
      assert.equal(conn._connectPromise, null); // not wedged — a fresh connect() can start again
    } finally { net.Socket = orig; }
  });
});

test('Node-3: a failed reconnect re-schedules the next attempt (backoff loop stays alive)', async () => {
  await withLiveEnv(async () => {
    const conn = new UnityConnection({ host: '127.0.0.1', port: 61999 });
    conn.connect = async () => { throw new Error('ECONNREFUSED'); }; // simulate the editor being down
    let scheduled = 0;
    const real = conn.scheduleReconnect.bind(conn);
    conn.scheduleReconnect = () => { scheduled++; real(); };
    try {
      conn.scheduleReconnect();                         // arm attempt 1 (fires at ~1s, backoff x2)
      await new Promise((r) => setTimeout(r, 3600));    // attempts fire at ~1s and ~3s, each re-arming on failure
      assert.ok(scheduled >= 3, `backoff loop should keep scheduling after failures, got ${scheduled}`);
    } finally {
      conn.disconnect(); // stop the loop (clears the pending reconnect timer)
    }
  });
});
