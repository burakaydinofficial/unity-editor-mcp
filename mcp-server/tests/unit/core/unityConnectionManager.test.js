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

  it('getActiveConnection targets the env/registry-resolved port', () => {
    const { mgr } = makeManager({ resolvePort: 6543 });
    const conn = mgr.getActiveConnection();
    assert.equal(conn.opts.port, 6543);
  });

  it('handshakes on connect and caches the manifest on editorInfo', async () => {
    const { mgr } = makeManager();
    const conn = mgr.getConnection('localhost', 7000);
    await conn.connect();
    // editorInfo is set asynchronously in the connected handler; allow the microtask to run.
    await new Promise((r) => setImmediate(r));
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

  it('setActiveInstance changes the default target', () => {
    const { mgr } = makeManager({ resolvePort: 6400 });
    mgr.setActiveInstance(7200);
    assert.equal(mgr.getActiveConnection().opts.port, 7200);
    assert.deepEqual(mgr.activeTarget(), { host: 'localhost', port: 7200 });
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
    await conn.connect();
    await new Promise((r) => setImmediate(r));
    const list = mgr.listConnections();
    assert.equal(list.length, 1);
    assert.equal(list[0].key, 'localhost:7000');
    assert.equal(list[0].connected, true);
    assert.ok(list[0].editorInfo);
  });
});
