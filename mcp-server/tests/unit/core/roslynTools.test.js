import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { isRoslynLifecycle, isRoslynGated, isRoslynCommand, mergeRoslynSurface, roslynDispatch, NOT_HANDLED } from '../../../src/core/roslynTools.js';

const readyMgr = (client) => ({ isReady: () => true, client: () => client, start: async () => 'ready', stop: async () => {}, statusOf: () => ({ state: 'ready', error: null }) });
const offMgr = () => ({ isReady: () => false, client: () => null, start: async () => 'unavailable', stop: async () => {}, statusOf: () => ({ state: 'off', error: null }) });

describe('roslynTools predicates', () => {
  it('classifies lifecycle / gated / other', () => {
    assert.equal(isRoslynLifecycle('start_roslyn'), true);
    assert.equal(isRoslynGated('rename_symbol'), true);
    assert.equal(isRoslynCommand('find_references'), false); // find_references is editor-cataloged, handled specially
    assert.equal(isRoslynCommand('ping'), false);
  });
});

describe('mergeRoslynSurface', () => {
  it('always lists the gated commands, annotated requires + available per state', () => {
    const off = mergeRoslynSurface([], 'h:1', offMgr());
    const rename = off.find((t) => t.name === 'rename_symbol');
    assert.ok(rename);
    assert.equal(rename.requires, 'roslyn');
    assert.equal(rename.available, false);

    const on = mergeRoslynSurface([], 'h:1', readyMgr({}));
    assert.equal(on.find((t) => t.name === 'rename_symbol').available, true);
  });

  it('lists the lifecycle commands as always available', () => {
    const s = mergeRoslynSurface([], 'h:1', offMgr());
    assert.equal(s.find((t) => t.name === 'start_roslyn').available, true);
  });
});

describe('roslynDispatch', () => {
  it('returns NOT_HANDLED for a non-Roslyn tool', async () => {
    const r = await roslynDispatch('ping', {}, {}, 'h:1', offMgr());
    assert.equal(r, NOT_HANDLED);
  });

  it('start_roslyn drives the manager and returns its state', async () => {
    let started = false;
    const mgr = { ...offMgr(), start: async () => { started = true; return 'unavailable'; } };
    const r = await roslynDispatch('start_roslyn', {}, { key: 'h:1' }, 'h:1', mgr);
    assert.equal(started, true);
    assert.equal(r.state, 'unavailable');
  });

  it('a gated command throws ROSLYN_NOT_READY when not ready', async () => {
    await assert.rejects(
      () => roslynDispatch('rename_symbol', {}, {}, 'h:1', offMgr()),
      /ROSLYN_NOT_READY|start_roslyn/,
    );
  });

  it('a gated command proxies to the sidecar client when ready', async () => {
    let got = null;
    const client = { call: async (tool, params) => { got = { tool, params }; return { ok: true }; } };
    const r = await roslynDispatch('rename_symbol', { path: 'A.cs' }, {}, 'h:1', readyMgr(client));
    assert.deepEqual(got, { tool: 'rename_symbol', params: { path: 'A.cs' } });
    assert.deepEqual(r, { ok: true });
  });

  it('find_references proxies semantic when ready, else NOT_HANDLED (falls through to the editor)', async () => {
    const client = { call: async () => ({ resolution: 'semantic', refs: [] }) };
    const ready = await roslynDispatch('find_references', { name: 'X' }, {}, 'h:1', readyMgr(client));
    assert.equal(ready.resolution, 'semantic');
    const off = await roslynDispatch('find_references', { name: 'X' }, {}, 'h:1', offMgr());
    assert.equal(off, NOT_HANDLED);
  });
});
