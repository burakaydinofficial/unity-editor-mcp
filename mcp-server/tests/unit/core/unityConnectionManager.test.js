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

  it('getActiveConnection returns a single env-resolving default (no pinned port, reused)', () => {
    const { mgr, created } = makeManager({ resolvePort: 6543 });
    const a = mgr.getActiveConnection();
    const b = mgr.getActiveConnection();
    assert.equal(a, b);
    assert.equal(a.opts.port, undefined); // env-resolves each connect; not pinned (re-resolution preserved)
    assert.equal(created.length, 1);
  });

  it('ensureReady connects, handshakes, and caches the manifest on editorInfo', async () => {
    const { mgr } = makeManager();
    const conn = mgr.getConnection('localhost', 7000);
    await mgr.ensureReady(conn);
    assert.equal(conn.connected, true);
    assert.ok(conn.editorInfo);
    assert.equal(conn.editorInfo.commands[0].name, 'ping');
  });

  it('resolveInstance handles a port, a project path, the active default, and the unknown case', () => {
    const { mgr } = makeManager({
      instances: [{ projectPath: 'C:/proj/A', port: 7100, host: 'localhost' }],
      resolvePort: 6400,
    });
    assert.deepEqual(mgr.resolveInstance(7100), { host: 'localhost', port: 7100 });
    assert.deepEqual(mgr.resolveInstance('7100'), { host: 'localhost', port: 7100 });
    assert.deepEqual(mgr.resolveInstance('C:/proj/A'), { host: 'localhost', port: 7100 });
    assert.deepEqual(mgr.resolveInstance(null), { host: 'localhost', port: 6400 }); // active default
    assert.equal(mgr.resolveInstance('C:/proj/missing'), null);
  });

  it('getConnectionForInstance routes to the resolved instance, null when unresolved', () => {
    const { mgr } = makeManager({ instances: [{ projectPath: 'C:/proj/A', port: 7100 }] });
    assert.equal(mgr.getConnectionForInstance('C:/proj/A').opts.port, 7100);
    assert.equal(mgr.getConnectionForInstance('C:/proj/missing'), null);
  });

  it('setActiveInstance pins the default, and null clears it back to env-resolving', () => {
    const { mgr } = makeManager({ resolvePort: 6400 });
    mgr.setActiveInstance(7200);
    assert.equal(mgr.getActiveConnection().opts.port, 7200); // pinned
    assert.deepEqual(mgr.activeTarget(), { host: 'localhost', port: 7200 });
    mgr.setActiveInstance(null);
    assert.equal(mgr.activeOverride, null);
    assert.equal(mgr.getActiveConnection().opts.port, undefined); // back to env-resolving
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

  it('prune never closes the active-override connection even when its editor is not live', () => {
    const { mgr } = makeManager({ instances: [] }); // registry empty -> nothing "live"
    mgr.setActiveInstance(7000);
    const overrideConn = mgr.getActiveConnection(); // pinned at localhost:7000
    const pruned = mgr.prune();
    assert.equal(pruned, 0);
    assert.equal(mgr.connections.has('localhost:7000'), true);
    assert.equal(overrideConn.disconnected, false);
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
});
