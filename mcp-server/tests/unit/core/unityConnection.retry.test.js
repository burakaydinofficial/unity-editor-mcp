import { describe, it, beforeEach, afterEach, mock } from 'node:test';
import assert from 'node:assert/strict';
import net from 'net';
import { EventEmitter } from 'events';
import { UnityConnection } from '../../../src/core/unityConnection.js';

// Exercises the REAL reconnection logic — scheduleReconnect's exponential backoff, the
// close-triggers-reconnect flow, and pending-command draining — against a MOCKED net.Socket
// (the same pattern as unityConnection.test.js), not real TCP.
//
// The previous version was vacuous: it pointed connect() at real servers via config.unity.port
// (which connect() never reads — it resolves via resolveTargetPort), spoke newline-delimited JSON
// instead of the length-prefix protocol, and replaced scheduleReconnect with a self-referential
// copy of the backoff formula, so it asserted its own arithmetic. (Audit findings.)
//
// connect() refuses under NODE_ENV=test / CI=true; those vars are cleared per-test (and restored)
// so connect() actually runs against the mock — making this deterministic and safe to gate in CI.
describe('UnityConnection reconnection', () => {
  let connection;
  let mockSocket;
  let originalSocket;
  let originalConfig;
  let savedEnv;

  beforeEach(async () => {
    savedEnv = { CI: process.env.CI, NODE_ENV: process.env.NODE_ENV, DISABLE_AUTO_RECONNECT: process.env.DISABLE_AUTO_RECONNECT };
    delete process.env.CI;
    delete process.env.NODE_ENV;
    delete process.env.DISABLE_AUTO_RECONNECT;

    const { config } = await import('../../../src/core/config.js');
    originalConfig = { ...config.unity };
    config.unity.reconnectDelay = 100;
    config.unity.maxReconnectDelay = 500;
    config.unity.reconnectBackoffMultiplier = 2;
    config.unity.commandTimeout = 1000;

    mockSocket = new EventEmitter();
    mockSocket.write = mock.fn((data, cb) => { if (cb) cb(); });
    mockSocket.destroy = mock.fn(() => {});
    mockSocket.connect = mock.fn(() => {}); // no auto-connect; tests emit 'connect' explicitly
    originalSocket = net.Socket;
    net.Socket = function () { return mockSocket; };

    connection = new UnityConnection();
  });

  afterEach(async () => {
    connection.isDisconnecting = true;
    if (connection.reconnectTimer) { clearTimeout(connection.reconnectTimer); connection.reconnectTimer = null; }
    if (connection.socket && connection.socket.removeAllListeners) connection.socket.removeAllListeners();
    connection.socket = null;
    net.Socket = originalSocket;
    const { config } = await import('../../../src/core/config.js');
    Object.assign(config.unity, originalConfig);
    for (const [k, v] of Object.entries(savedEnv)) {
      if (v === undefined) delete process.env[k]; else process.env[k] = v;
    }
    mock.restoreAll();
  });

  // Drive connect() to success against the mock socket.
  async function connect() {
    const p = connection.connect();
    process.nextTick(() => mockSocket.emit('connect'));
    await p;
  }

  describe('scheduleReconnect backoff', () => {
    it('grows exponentially and caps at maxReconnectDelay (real scheduleReconnect)', () => {
      const delays = [];
      const realSetTimeout = global.setTimeout;
      global.setTimeout = (fn, delay) => { delays.push(delay); return { __fake: true }; };
      try {
        for (let attempt = 0; attempt < 5; attempt++) {
          connection.reconnectTimer = null; // allow another schedule
          connection.reconnectAttempts = attempt;
          connection.scheduleReconnect();
        }
      } finally {
        global.setTimeout = realSetTimeout;
        connection.reconnectTimer = null;
      }
      // 100*2^0, 100*2^1, 100*2^2 = 100,200,400; then 800 and 1600 both capped to 500.
      assert.deepEqual(delays, [100, 200, 400, 500, 500]);
    });

    it('does not schedule a second timer while one is already pending', () => {
      let calls = 0;
      const realSetTimeout = global.setTimeout;
      global.setTimeout = () => { calls++; return { __fake: true }; };
      try {
        connection.reconnectTimer = null;
        connection.scheduleReconnect(); // schedules
        connection.scheduleReconnect(); // reconnectTimer truthy -> early return
      } finally {
        global.setTimeout = realSetTimeout;
        connection.reconnectTimer = null;
      }
      assert.equal(calls, 1);
    });
  });

  describe('on unexpected close', () => {
    it('marks disconnected, drains pending commands, and schedules a reconnect', async () => {
      await connect();
      assert.equal(connection.connected, true);

      const pending = connection.sendCommand('slow', {});
      assert.equal(connection.pendingCommands.size, 1);

      let scheduled = 0;
      connection.scheduleReconnect = () => { scheduled++; };

      mockSocket.emit('close');

      await assert.rejects(pending, /Connection closed/);
      assert.equal(connection.connected, false);
      assert.equal(connection.pendingCommands.size, 0);
      assert.equal(scheduled, 1);
    });

    it('does not schedule a reconnect when DISABLE_AUTO_RECONNECT=true', async () => {
      await connect();
      process.env.DISABLE_AUTO_RECONNECT = 'true';
      let scheduled = 0;
      connection.scheduleReconnect = () => { scheduled++; };
      mockSocket.emit('close');
      assert.equal(scheduled, 0);
    });
  });

  describe('intentional disconnect', () => {
    it('clears the reconnect timer and does not reconnect on the resulting close', async () => {
      await connect();
      let scheduled = 0;
      connection.scheduleReconnect = () => { scheduled++; };
      connection.disconnect(); // removes listeners, destroys + nulls the socket
      mockSocket.emit('close'); // listeners removed -> no-op
      assert.equal(scheduled, 0);
      assert.equal(connection.reconnectTimer, null);
      assert.equal(connection.connected, false);
    });
  });

  describe('successful (re)connect', () => {
    it('resets reconnectAttempts to 0', async () => {
      connection.reconnectAttempts = 5;
      await connect();
      assert.equal(connection.reconnectAttempts, 0);
    });
  });

  describe('sendCommand without a connection', () => {
    it('rejects with "Not connected"', async () => {
      await assert.rejects(connection.sendCommand('test', {}), /Not connected to Unity/);
    });
  });
});
