import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { ListUnityInstancesToolHandler } from '../../../../src/handlers/instances/ListUnityInstancesToolHandler.js';

// A registry descriptor; __live drives the injected isLive() and is NOT a real field.
const inst = (over = {}) => ({
  projectPath: 'C:/proj/A', unityVersion: '2020.3.49f1', port: 6500, pid: 111,
  protocolVersion: '1.0.0', host: 'h', startedAt: 's', lastHeartbeat: 'l', __live: true, ...over,
});

// Injected fake of the discovery module (only the 4 functions the handler uses).
const fakeDeps = (instances, activePort = null) => ({
  defaultRegistryDirectory: () => '/fake/registry',
  readInstances: () => instances,
  isLive: (d) => d.__live === true,
  resolveUnityPort: () => activePort,
});

describe('ListUnityInstancesToolHandler', () => {
  it('constructor sets name, description, and schema', () => {
    const h = new ListUnityInstancesToolHandler({}, null, fakeDeps([]));
    assert.equal(h.name, 'list_unity_instances');
    assert.ok(h.description.length > 0);
    assert.deepEqual(h.inputSchema.required, []);
    assert.equal(h.inputSchema.properties.includeStale.type, 'boolean');
  });

  it('defaults deps to the discovery module when constructed via createHandlers (manager is arg 2)', () => {
    // Regression for the v0.3.0 critical: createHandlers calls new Handler(unityConnection, manager).
    // deps must default to the real discovery module, NOT receive the manager (which has no
    // readInstances) — otherwise the shipped tool throws at runtime.
    const fakeManager = { getActiveConnection() {} };
    const h = new ListUnityInstancesToolHandler({}, fakeManager);
    assert.equal(typeof h.deps.readInstances, 'function');
    assert.equal(typeof h.deps.defaultRegistryDirectory, 'function');
    assert.notEqual(h.deps, fakeManager);
  });

  it('returns only live instances by default', async () => {
    const h = new ListUnityInstancesToolHandler({}, null, fakeDeps([
      inst({ projectPath: 'C:/proj/A', __live: true }),
      inst({ projectPath: 'C:/proj/B', __live: false }),
    ]));
    const r = await h.execute({});
    assert.equal(r.count, 1);
    assert.equal(r.instances[0].projectPath, 'C:/proj/A');
    assert.equal(r.instances[0].live, true);
  });

  it('includes stale descriptors when includeStale=true', async () => {
    const h = new ListUnityInstancesToolHandler({}, null, fakeDeps([
      inst({ projectPath: 'C:/proj/A', __live: true }),
      inst({ projectPath: 'C:/proj/B', __live: false }),
    ]));
    const r = await h.execute({ includeStale: true });
    assert.equal(r.count, 2);
    assert.equal(r.instances.find((i) => i.projectPath === 'C:/proj/B').live, false);
  });

  it('marks the active instance by resolved port', async () => {
    const h = new ListUnityInstancesToolHandler({}, null, fakeDeps([
      inst({ projectPath: 'C:/proj/A', port: 6500, __live: true }),
      inst({ projectPath: 'C:/proj/B', port: 6600, __live: true }),
    ], 6600));
    const r = await h.execute({});
    assert.equal(r.activePort, 6600);
    assert.equal(r.instances.find((i) => i.port === 6600).active, true);
    assert.equal(r.instances.find((i) => i.port === 6500).active, false);
  });

  it('sorts by projectPath and exposes only known fields', async () => {
    const h = new ListUnityInstancesToolHandler({}, null, fakeDeps([
      inst({ projectPath: 'C:/proj/Z', __live: true }),
      inst({ projectPath: 'C:/proj/A', __live: true }),
    ]));
    const r = await h.execute({});
    assert.deepEqual(r.instances.map((i) => i.projectPath), ['C:/proj/A', 'C:/proj/Z']);
    assert.ok(!('__live' in r.instances[0]));
  });

  it('empty registry yields an empty list and reports the registry dir', async () => {
    const h = new ListUnityInstancesToolHandler({}, null, fakeDeps([]));
    const r = await h.execute({});
    assert.equal(r.count, 0);
    assert.deepEqual(r.instances, []);
    assert.equal(r.registryDir, '/fake/registry');
  });

  it('never marks a dead instance active even when its port matches activePort', async () => {
    const h = new ListUnityInstancesToolHandler({}, null, fakeDeps([
      inst({ projectPath: 'C:/proj/A', port: 6600, __live: false }),
    ], 6600));
    const r = await h.execute({ includeStale: true });
    assert.equal(r.instances[0].live, false);
    assert.equal(r.instances[0].active, false);
  });

  it('a throwing resolveUnityPort does not break listing', async () => {
    const deps = fakeDeps([inst({ __live: true })]);
    deps.resolveUnityPort = () => { throw new Error('boom'); };
    const h = new ListUnityInstancesToolHandler({}, null, deps);
    const r = await h.execute({});
    assert.equal(r.count, 1);
    assert.equal(r.activePort, null);
  });

  it('handle() wraps the result in the success envelope', async () => {
    const h = new ListUnityInstancesToolHandler({}, null, fakeDeps([inst()]));
    const res = await h.handle({});
    assert.equal(res.status, 'success');
    assert.equal(res.result.count, 1);
  });
});
