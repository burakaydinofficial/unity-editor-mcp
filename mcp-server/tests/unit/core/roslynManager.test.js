import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { RoslynManager, ROSLYN_STATES } from '../../../src/core/roslynManager.js';

describe('RoslynManager', () => {
  it('defaults an unknown instance to "off"', () => {
    const m = new RoslynManager();
    assert.equal(m.getState('h:1'), ROSLYN_STATES.OFF);
    assert.equal(m.isReady('h:1'), false);
    assert.equal(m.client('h:1'), null);
  });

  it('start() with the default factory (no sidecar) resolves to "unavailable"', async () => {
    const m = new RoslynManager(); // default factory returns null
    const state = await m.start('h:1', {});
    assert.equal(state, ROSLYN_STATES.UNAVAILABLE);
    assert.equal(m.isReady('h:1'), false);
    assert.match(m.statusOf('h:1').error, /not installed/i);
  });

  it('start() with a client factory reaches "ready" and exposes the client', async () => {
    const fakeClient = { id: 'sidecar' };
    const m = new RoslynManager(async () => fakeClient);
    const state = await m.start('h:1', {});
    assert.equal(state, ROSLYN_STATES.READY);
    assert.equal(m.isReady('h:1'), true);
    assert.equal(m.client('h:1'), fakeClient);
  });

  it('isolates state per instance key', async () => {
    const m = new RoslynManager(async () => ({ ok: true }));
    await m.start('h:1', {});
    assert.equal(m.isReady('h:1'), true);
    assert.equal(m.isReady('h:2'), false);
  });

  it('stop() disposes the client and resets to "off"', async () => {
    let disposed = false;
    const m = new RoslynManager(async () => ({ dispose: async () => { disposed = true; } }));
    await m.start('h:1', {});
    await m.stop('h:1');
    assert.equal(disposed, true);
    assert.equal(m.getState('h:1'), ROSLYN_STATES.OFF);
  });

  it('start() is idempotent while ready', async () => {
    let calls = 0;
    const m = new RoslynManager(async () => { calls++; return { n: calls }; });
    await m.start('h:1', {});
    await m.start('h:1', {});
    assert.equal(calls, 1);
  });

  it('stop() during indexing disposes the late-arriving client instead of stranding it', async () => {
    let disposed = false;
    let resolveFactory;
    const m = new RoslynManager(() => new Promise((r) => { resolveFactory = r; }));
    const startP = m.start('h:1', {});           // suspends at the factory await (INDEXING)
    await m.stop('h:1');                          // races: deletes the slot while indexing
    resolveFactory({ dispose: async () => { disposed = true; } }); // factory now resolves
    await startP;
    assert.equal(disposed, true, 'the late client must be disposed, not stranded');
    assert.equal(m.getState('h:1'), ROSLYN_STATES.OFF);
    assert.equal(m.client('h:1'), null);
  });
});
