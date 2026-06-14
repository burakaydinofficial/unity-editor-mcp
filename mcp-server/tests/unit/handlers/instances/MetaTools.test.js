import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { ListUnityToolsToolHandler } from '../../../../src/handlers/instances/ListUnityToolsToolHandler.js';
import { CallUnityToolToolHandler } from '../../../../src/handlers/instances/CallUnityToolToolHandler.js';
import { SetActiveUnityInstanceToolHandler } from '../../../../src/handlers/instances/SetActiveUnityInstanceToolHandler.js';

const manifest = [
  { name: 'ping', category: 'system', description: 'Test connection', params: { type: 'object', properties: { message: { type: 'string' } }, required: [] } },
  { name: 'create_gameobject', category: 'gameobject', description: 'Create a GO', params: { type: 'object', properties: { name: { type: 'string' }, primitiveType: { type: 'string' } }, required: ['name'] } },
];

const fakeConn = (sendImpl) => ({
  editorInfo: { commands: manifest },
  isConnected: () => true,
  sendCommand: sendImpl || (async (type, p) => ({ ok: true, type, p })),
});

const fakeManager = (over = {}) => ({
  getConnectionForInstance: over.getConnectionForInstance || (() => over.conn || fakeConn()),
  ensureReady: over.ensureReady || (async (c) => c),
  setActiveInstance: over.setActiveInstance || (() => ({ host: 'localhost', port: 7000 })),
});

describe('list_unity_tools', () => {
  it('returns name/category/description by default (no params — lazy)', async () => {
    const h = new ListUnityToolsToolHandler({}, fakeManager());
    const r = await h.execute({});
    assert.equal(r.count, 2);
    assert.deepEqual(r.tools.map((t) => t.name).sort(), ['create_gameobject', 'ping']);
    assert.equal(r.tools.find((t) => t.name === 'ping').category, 'system');
    assert.ok(!('params' in r.tools[0]));
  });

  it('filters by category', async () => {
    const h = new ListUnityToolsToolHandler({}, fakeManager());
    const r = await h.execute({ category: 'gameobject' });
    assert.equal(r.count, 1);
    assert.equal(r.tools[0].name, 'create_gameobject');
  });

  it('returns one tool full schema when name is given', async () => {
    const h = new ListUnityToolsToolHandler({}, fakeManager());
    const r = await h.execute({ name: 'create_gameobject' });
    assert.equal(r.tool.name, 'create_gameobject');
    assert.deepEqual(r.tool.params.required, ['name']);
  });

  it('errors on an unresolved instance', async () => {
    const h = new ListUnityToolsToolHandler({}, fakeManager({ getConnectionForInstance: () => null }));
    const res = await h.handle({ instance: 'C:/missing' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /No Unity instance/);
  });

  it('errors on an unknown tool name', async () => {
    const h = new ListUnityToolsToolHandler({}, fakeManager());
    const res = await h.handle({ name: 'nope' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /not available/);
  });
});

describe('call_unity_tool', () => {
  it('validates params then routes to sendCommand', async () => {
    let sent;
    const conn = fakeConn(async (type, p) => { sent = { type, p }; return { created: true }; });
    const h = new CallUnityToolToolHandler({}, fakeManager({ conn }));
    const r = await h.execute({ tool: 'create_gameobject', params: { name: 'Cube' } });
    assert.deepEqual(sent, { type: 'create_gameobject', p: { name: 'Cube' } });
    assert.deepEqual(r, { created: true });
  });

  it('rejects invalid params with a field-pathed error and never calls sendCommand', async () => {
    let called = false;
    const conn = fakeConn(async () => { called = true; return {}; });
    const h = new CallUnityToolToolHandler({}, fakeManager({ conn }));
    const res = await h.handle({ tool: 'create_gameobject', params: {} }); // missing required "name"
    assert.equal(res.status, 'error');
    assert.match(res.error, /params.*required.*name/i);
    assert.equal(called, false);
  });

  it('errors on an unknown tool', async () => {
    const h = new CallUnityToolToolHandler({}, fakeManager());
    const res = await h.handle({ tool: 'nope' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /not available/);
  });

  it('requires the tool param (BaseToolHandler validation)', async () => {
    const h = new CallUnityToolToolHandler({}, fakeManager());
    const res = await h.handle({});
    assert.equal(res.status, 'error');
  });

  it('rejects an empty/whitespace tool name', async () => {
    const h = new CallUnityToolToolHandler({}, fakeManager());
    const res = await h.handle({ tool: '   ' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /non-empty/);
  });

  it('errors on an unresolved instance', async () => {
    const h = new CallUnityToolToolHandler({}, fakeManager({ getConnectionForInstance: () => null }));
    const res = await h.handle({ tool: 'ping', instance: 'C:/missing' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /No Unity instance/);
  });
});

describe('graceful degradation (editor without a rich manifest)', () => {
  // An older package build advertises availableCommands (names) only — no rich `commands` manifest.
  const degradedConn = (sendImpl) => ({
    editorInfo: { availableCommands: ['ping', 'get_editor_state'] },
    isConnected: () => true,
    sendCommand: sendImpl || (async (type, p) => ({ ok: true, type, p })),
  });

  it('list_unity_tools falls back to names with schemasAvailable:false', async () => {
    const h = new ListUnityToolsToolHandler({}, fakeManager({ conn: degradedConn() }));
    const r = await h.execute({});
    assert.equal(r.schemasAvailable, false);
    assert.deepEqual(r.tools.map((t) => t.name).sort(), ['get_editor_state', 'ping']);
    assert.equal(r.tools[0].category, null);
  });

  it('call_unity_tool invokes a names-only tool, passing params through without schema validation', async () => {
    let sent;
    const conn = degradedConn(async (type, p) => { sent = { type, p }; return { state: {} }; });
    const h = new CallUnityToolToolHandler({}, fakeManager({ conn }));
    const r = await h.execute({ tool: 'get_editor_state', params: { anything: 123 } }); // would fail a schema; no schema here
    assert.deepEqual(sent, { type: 'get_editor_state', p: { anything: 123 } });
    assert.deepEqual(r, { state: {} });
  });

  it('call_unity_tool still rejects a tool the editor does not advertise', async () => {
    const h = new CallUnityToolToolHandler({}, fakeManager({ conn: degradedConn() }));
    const res = await h.handle({ tool: 'not_a_real_tool' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /not available/);
  });
});

describe('set_active_unity_instance', () => {
  it('sets the active instance', async () => {
    const h = new SetActiveUnityInstanceToolHandler({}, fakeManager({ setActiveInstance: (ref) => ({ host: 'localhost', port: Number(ref) }) }));
    const r = await h.execute({ instance: '7200' });
    assert.deepEqual(r.active, { host: 'localhost', port: 7200 });
  });

  it('resets to the default when instance is omitted', async () => {
    let arg = 'unset';
    const h = new SetActiveUnityInstanceToolHandler({}, fakeManager({ setActiveInstance: (ref) => { arg = ref; return null; } }));
    const r = await h.execute({});
    assert.equal(arg, null);
    assert.equal(r.active, null);
  });

  it('errors when a named instance is not found', async () => {
    const h = new SetActiveUnityInstanceToolHandler({}, fakeManager({ setActiveInstance: () => null }));
    const res = await h.handle({ instance: 'C:/missing' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /No Unity instance/);
  });
});
