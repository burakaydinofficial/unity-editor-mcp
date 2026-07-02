import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'events';
import { UnityConnectionManager } from '../../../src/core/unityConnectionManager.js';

class FakeConn extends EventEmitter {
  constructor(opts) { super(); this.opts = opts; this.connected = false; this.disconnected = false; this.editorInfo = null; }
  isConnected() { return this.connected; }
  async connect() { this.connected = true; this.emit('connected'); }
  disconnect() { this.disconnected = true; this.connected = false; }
}

function makeManager({ instances = [], resolvePort = 6400 } = {}) {
  const created = [];
  const mgr = new UnityConnectionManager({
    createConnection: (opts) => { const c = new FakeConn(opts); created.push(c); return c; },
    performHandshake: async () => ({ performed: true, compatible: true, handshake: { commands: [{ name: 'ping' }] } }),
    discovery: {
      resolveUnityPort: () => resolvePort,
      defaultRegistryDirectory: () => '/reg',
      findInstanceByProjectPath: (_dir, p) => instances.find((i) => i.projectPath === p) || null,
      readInstances: () => instances,
      isLive: (d) => d.__live !== false,
    },
    env: {},
    host: 'localhost',
  });
  return { mgr, created };
}

describe('UnityConnectionManager', () => {
  it('lazily creates and reuses a connection per host:port', () => {
    const { mgr, created } = makeManager();
    const a = mgr.getConnection('localhost', 7000);
    const b = mgr.getConnection('localhost', 7000);
    assert.equal(a, b);
    assert.equal(created.length, 1);
    assert.deepEqual(a.opts, { host: 'localhost', port: 7000 });
  });

  it('keeps distinct connections for distinct ports', () => {
    const { mgr, created } = makeManager();
    mgr.getConnection('localhost', 7000);
    mgr.getConnection('localhost', 7001);
    assert.equal(created.length, 2);
    assert.equal(mgr.connections.size, 2);
  });

  // ADR 0006: no default instance — requireConnection resolves an EXPLICIT ref or throws.
  it('requireConnection throws a clear error on a missing/empty/whitespace ref', () => {
    const { mgr } = makeManager();
    for (const ref of [null, undefined, '', '   ']) {
      assert.throws(() => mgr.requireConnection(ref), /instance is required/);
    }
  });

  it('requireConnection throws "No Unity instance found" on an unresolved project-path ref', () => {
    const { mgr } = makeManager({ instances: [] });
    assert.throws(() => mgr.requireConnection('C:/missing'), /No Unity instance found/);
  });

  it('requireConnection returns the PINNED connection for a port ref', () => {
    const { mgr } = makeManager();
    const conn = mgr.requireConnection('7200');
    assert.equal(conn.opts.port, 7200); // pinned to the named port (no env-resolved default exists)
  });

  // Bug hunt Node-1: a same-host editor binds loopback, so a project-path ref must connect via the CONFIGURED host,
  // not the registry descriptor's machine-name (which resolves to a LAN address the loopback listener never answers).
  it('resolveInstance connects via the configured host, not the registry machine-name', () => {
    const { mgr } = makeManager({ instances: [{ projectPath: 'C:/proj/A', host: 'MACHINE16', port: 7400 }] });
    assert.deepEqual(mgr.resolveInstance('C:/proj/A'), { host: 'localhost', port: 7400 });
  });

  // Bug hunt Node-6: a stale (dead-editor) descriptor must not resolve into a 30s connect stall.
  it('resolveInstance returns null for a non-live registry descriptor', () => {
    const { mgr } = makeManager({ instances: [{ projectPath: 'C:/proj/A', host: 'MACHINE16', port: 7400, __live: false }] });
    assert.equal(mgr.resolveInstance('C:/proj/A'), null);
  });

  // Bug hunt Node-2: prune keyed the live set by desc.host (machine-name), never matching a port-ref connection's
  // `localhost:port` key — so it disconnected live, in-use connections on every list_unity_instances.
  it('prune does NOT drop a live port-ref connection when the registry keys by machine-name', () => {
    const { mgr } = makeManager({ instances: [{ projectPath: 'C:/proj/A', host: 'MACHINE16', port: 6858 }] });
    const conn = mgr.requireConnection('6858'); // keyed localhost:6858
    assert.equal(mgr.connections.size, 1);
    assert.equal(mgr.prune(), 0);
    assert.equal(conn.disconnected, false);
    assert.equal(mgr.connections.size, 1);
  });

  // Bug hunt Node-7: a first handshake that yields no manifest (e.g. it timed out while the editor was compiling) must
  // not strand the connection forever — ensureReady re-issues it instead of awaiting the memoized failure.
  it('ensureReady re-issues the handshake when the first produced no manifest', async () => {
    let calls = 0;
    const mgr = new UnityConnectionManager({
      createConnection: (opts) => new FakeConn(opts),
      performHandshake: async () => {
        calls++;
        return calls === 1
          ? { performed: true, compatible: true, handshake: null }         // 1st: no manifest
          : { performed: true, compatible: true, handshake: { commands: [] } }; // retry: manifest
      },
      discovery: { defaultRegistryDirectory: () => '/reg', findInstanceByProjectPath: () => null, readInstances: () => [], isLive: () => true },
      env: {}, host: 'localhost',
    });
    const conn = mgr.getConnection('localhost', 7500);
    await mgr.ensureReady(conn);
    assert.equal(calls, 2);       // the null-manifest handshake was retried
    assert.ok(conn.editorInfo);   // and the retry populated the manifest
  });

  it('requireConnection resolves a project-path ref via the registry to the pinned connection', () => {
    const { mgr } = makeManager({ instances: [{ projectPath: 'C:/proj/A', port: 7300 }] });
    const conn = mgr.requireConnection('C:/proj/A');
    assert.equal(conn.opts.port, 7300);
  });

  it('ensureReady connects, handshakes, and caches the manifest on editorInfo', async () => {
    const { mgr } = makeManager();
    const conn = mgr.getConnection('localhost', 7000);
    await mgr.ensureReady(conn);
    assert.equal(conn.connected, true);
    assert.ok(conn.editorInfo);
    assert.equal(conn.editorInfo.commands[0].name, 'ping');
  });

  it('resolveInstance handles a port, a project path, null, and the unknown case', () => {
    const { mgr } = makeManager({
      instances: [{ projectPath: 'C:/proj/A', port: 7100, host: 'localhost' }],
      resolvePort: 6400,
    });
    assert.deepEqual(mgr.resolveInstance(7100), { host: 'localhost', port: 7100 });
    assert.deepEqual(mgr.resolveInstance('7100'), { host: 'localhost', port: 7100 });
    assert.deepEqual(mgr.resolveInstance('C:/proj/A'), { host: 'localhost', port: 7100 });
    assert.equal(mgr.resolveInstance(null), null); // no default instance (ADR 0006)
    assert.equal(mgr.resolveInstance('C:/proj/missing'), null);
  });

  it('prune closes + drops connections whose editor is no longer live', () => {
    const { mgr } = makeManager({
      instances: [{ projectPath: 'C:/A', port: 7000, host: 'localhost', __live: true }],
    });
    const live = mgr.getConnection('localhost', 7000);
    const dead = mgr.getConnection('localhost', 7001); // not in the registry -> not live
    const pruned = mgr.prune();
    assert.equal(pruned, 1);
    assert.equal(mgr.connections.has('localhost:7000'), true);
    assert.equal(mgr.connections.has('localhost:7001'), false);
    assert.equal(dead.disconnected, true);
    assert.equal(live.disconnected, false);
  });

  it('disconnectAll closes and clears every connection', () => {
    const { mgr, created } = makeManager();
    mgr.getConnection('localhost', 7000);
    mgr.getConnection('localhost', 7001);
    mgr.disconnectAll();
    assert.equal(mgr.connections.size, 0);
    assert.ok(created.every((c) => c.disconnected));
  });

  it('listConnections reports key, connected, and editorInfo', async () => {
    const { mgr } = makeManager();
    const conn = mgr.getConnection('localhost', 7000);
    await mgr.ensureReady(conn);
    const list = mgr.listConnections();
    assert.equal(list.length, 1);
    assert.equal(list[0].key, 'localhost:7000');
    assert.equal(list[0].connected, true);
    assert.ok(list[0].editorInfo);
  });

  it('attaches an error listener to every connection (no unhandled-error crash)', () => {
    const { mgr } = makeManager();
    const conn = mgr.getConnection('localhost', 7000);
    assert.ok(conn.listenerCount('error') >= 1);
  });

  it('resets editorInfo to null when the (re)connect handshake fails', async () => {
    const mgr = new UnityConnectionManager({
      createConnection: (opts) => new FakeConn(opts),
      performHandshake: async () => { throw new Error('handshake boom'); },
      discovery: { resolveUnityPort: () => 6400, defaultRegistryDirectory: () => '/reg', findInstanceByProjectPath: () => null, readInstances: () => [], isLive: () => true },
      env: {},
      host: 'localhost',
    });
    const conn = mgr.getConnection('localhost', 7000);
    conn.editorInfo = { commands: [{ name: 'stale' }] }; // a prior session's manifest
    await mgr.ensureReady(conn);
    assert.equal(conn.editorInfo, null); // failed handshake must not leave the phantom manifest
  });

  it('concurrent ensureReady calls both resolve with the manifest set', async () => {
    const { mgr } = makeManager();
    const conn = mgr.getConnection('localhost', 7000);
    await Promise.all([mgr.ensureReady(conn), mgr.ensureReady(conn)]);
    assert.equal(conn.connected, true);
    assert.ok(conn.editorInfo);
    assert.equal(conn.editorInfo.commands[0].name, 'ping');
  });
});
