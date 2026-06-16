import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { ListUnityToolsToolHandler } from '../../../../src/handlers/instances/ListUnityToolsToolHandler.js';
import { CallUnityToolToolHandler } from '../../../../src/handlers/instances/CallUnityToolToolHandler.js';
import { CreateScriptToolHandler } from '../../../../src/handlers/scripting/CreateScriptToolHandler.js';

const manifest = [
  { name: 'ping', category: 'system', description: 'Test connection', params: { type: 'object', properties: { message: { type: 'string' } }, required: [] } },
  { name: 'create_gameobject', category: 'gameobject', description: 'Create a GO', params: { type: 'object', properties: { name: { type: 'string' }, primitiveType: { type: 'string' } }, required: ['name'] } },
];

const fakeConn = (sendImpl) => ({
  editorInfo: { commands: manifest },
  isConnected: () => true,
  sendCommand: sendImpl || (async (type, p) => ({ ok: true, type, p })),
});

// ADR 0006: no default instance — requireConnection resolves an EXPLICIT instance or throws.
const fakeManager = (over = {}) => ({
  requireConnection: over.requireConnection || (() => over.conn || fakeConn()),
  ensureReady: over.ensureReady || (async (c) => c),
});

// A manager whose requireConnection throws the no-instance / unresolved errors, like the real one.
const throwingManager = (msg) => ({
  requireConnection: () => { throw new Error(msg); },
  ensureReady: async (c) => c,
});

describe('list_unity_tools', () => {
  it('returns name/category/description by default (no extra params — lazy)', async () => {
    const h = new ListUnityToolsToolHandler(fakeManager());
    const r = await h.execute({ instance: '7000' });
    const names = r.tools.map((t) => t.name);
    assert.ok(names.includes('create_gameobject') && names.includes('ping'), 'editor tools present');
    assert.equal(r.tools.find((t) => t.name === 'ping').category, 'system');
    assert.ok(!('params' in r.tools.find((t) => t.name === 'ping')));
    // Roslyn capability commands are advertised dynamically (Plan 2); gated ones carry requires + available.
    const rename = r.tools.find((t) => t.name === 'rename_symbol');
    assert.ok(rename && rename.requires === 'roslyn' && rename.available === false);
  });

  it('filters by category', async () => {
    const h = new ListUnityToolsToolHandler(fakeManager());
    const r = await h.execute({ instance: '7000', category: 'gameobject' });
    assert.equal(r.count, 1);
    assert.equal(r.tools[0].name, 'create_gameobject');
  });

  it('returns one tool full schema when name is given', async () => {
    const h = new ListUnityToolsToolHandler(fakeManager());
    const r = await h.execute({ instance: '7000', name: 'create_gameobject' });
    assert.equal(r.tool.name, 'create_gameobject');
    assert.deepEqual(r.tool.params.required, ['name']);
  });

  it('requires an instance (no default — ADR 0006)', async () => {
    const h = new ListUnityToolsToolHandler(fakeManager());
    const res = await h.handle({});
    assert.equal(res.status, 'error');
    assert.match(res.error, /instance/i);
  });

  it('errors on an unresolved instance', async () => {
    const h = new ListUnityToolsToolHandler(throwingManager('No Unity instance found for "C:/missing".'));
    const res = await h.handle({ instance: 'C:/missing' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /No Unity instance/);
  });

  it('errors on an unknown tool name', async () => {
    const h = new ListUnityToolsToolHandler(fakeManager());
    const res = await h.handle({ instance: '7000', name: 'nope' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /not available/);
  });
});

describe('call_unity_tool', () => {
  it('validates params then routes to sendCommand', async () => {
    let sent;
    const conn = fakeConn(async (type, p) => { sent = { type, p }; return { created: true }; });
    const h = new CallUnityToolToolHandler(fakeManager({ conn }));
    const r = await h.execute({ instance: '7000', tool: 'create_gameobject', params: { name: 'Cube' } });
    assert.deepEqual(sent, { type: 'create_gameobject', p: { name: 'Cube' } });
    assert.deepEqual(r, { created: true });
  });

  it('rejects invalid params with a field-pathed error and never calls sendCommand', async () => {
    let called = false;
    const conn = fakeConn(async () => { called = true; return {}; });
    const h = new CallUnityToolToolHandler(fakeManager({ conn }));
    const res = await h.handle({ instance: '7000', tool: 'create_gameobject', params: {} }); // missing required "name"
    assert.equal(res.status, 'error');
    assert.match(res.error, /params.*required.*name/i);
    assert.equal(called, false);
  });

  it('errors on an unknown tool', async () => {
    const h = new CallUnityToolToolHandler(fakeManager());
    const res = await h.handle({ instance: '7000', tool: 'nope' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /not available/);
  });

  it('rejects a non-object params (string/array) before sending', async () => {
    let called = false;
    const conn = fakeConn(async () => { called = true; return {}; });
    const h = new CallUnityToolToolHandler(fakeManager({ conn }));
    const res = await h.handle({ instance: '7000', tool: 'ping', params: 'oops' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /params must be an object/);
    assert.equal(called, false);

    // The array branch is independently necessary (an array spreads into a char-indexed object).
    const res2 = await h.handle({ instance: '7000', tool: 'ping', params: ['Cube'] });
    assert.equal(res2.status, 'error');
    assert.match(res2.error, /params must be an object/);
    assert.equal(called, false);
  });

  it('requires the instance param (no default — ADR 0006)', async () => {
    const h = new CallUnityToolToolHandler(fakeManager());
    const res = await h.handle({ tool: 'ping' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /instance/i);
  });

  it('requires the tool param', async () => {
    const h = new CallUnityToolToolHandler(fakeManager());
    const res = await h.handle({ instance: '7000' });
    assert.equal(res.status, 'error');
  });

  it('rejects an empty/whitespace tool name', async () => {
    const h = new CallUnityToolToolHandler(fakeManager());
    const res = await h.handle({ instance: '7000', tool: '   ' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /non-empty/);
  });

  it('errors on an unresolved instance', async () => {
    const h = new CallUnityToolToolHandler(throwingManager('No Unity instance found for "C:/missing".'));
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
    const h = new ListUnityToolsToolHandler(fakeManager({ conn: degradedConn() }));
    const r = await h.execute({ instance: '7000' });
    assert.equal(r.schemasAvailable, false);
    const names = r.tools.map((t) => t.name);
    assert.ok(names.includes('get_editor_state') && names.includes('ping'), 'editor names present');
    assert.equal(r.tools.find((t) => t.name === 'ping').category, null);
    assert.ok(names.includes('start_roslyn'), 'Roslyn lifecycle is advertised even in degraded mode');
  });

  it('call_unity_tool invokes a names-only tool, passing params through without schema validation', async () => {
    let sent;
    const conn = degradedConn(async (type, p) => { sent = { type, p }; return { state: {} }; });
    const h = new CallUnityToolToolHandler(fakeManager({ conn }));
    const r = await h.execute({ instance: '7000', tool: 'get_editor_state', params: { anything: 123 } }); // would fail a schema; no schema here
    assert.deepEqual(sent, { type: 'get_editor_state', p: { anything: 123 } });
    assert.deepEqual(r, { state: {} });
  });

  it('call_unity_tool still rejects a tool the editor does not advertise', async () => {
    const h = new CallUnityToolToolHandler(fakeManager({ conn: degradedConn() }));
    const res = await h.handle({ instance: '7000', tool: 'not_a_real_tool' });
    assert.equal(res.status, 'error');
    assert.match(res.error, /not available/);
  });
});

describe('Node-logic routing (ADR 0006)', () => {
  it('list_unity_tools overrides a Node-logic tool with its Node handler schema', async () => {
    const conn = {
      editorInfo: { commands: [{ name: 'create_script', category: 'scripting', description: 'editor', params: { type: 'object', properties: { scriptContent: { type: 'string' } } } }] },
      isConnected: () => true,
      sendCommand: async () => ({}),
    };
    const h = new ListUnityToolsToolHandler(fakeManager({ conn }));
    const r = await h.execute({ instance: '7000', name: 'create_script' });
    const nodeDef = new CreateScriptToolHandler(null).getDefinition();
    assert.equal(r.tool.description, nodeDef.description);
    assert.deepEqual(r.tool.params, nodeDef.inputSchema);
    assert.equal(r.tool.result, null); // Node-logic tools carry no editor result schema — uniform null
  });

  it('list_unity_tools(name) surfaces the editor result-field hint (ADR 0006)', async () => {
    const resultSchema = { type: 'object', properties: { objects: { type: 'array' }, count: { type: 'number' } } };
    const conn = {
      editorInfo: { commands: [{ name: 'get_hierarchy', category: 'gameobject', description: 'h', params: { type: 'object' }, result: resultSchema }] },
      isConnected: () => true,
      sendCommand: async () => ({}),
    };
    const h = new ListUnityToolsToolHandler(fakeManager({ conn }));
    const r = await h.execute({ instance: '7000', name: 'get_hierarchy' });
    assert.deepEqual(r.tool.result, resultSchema); // the agent reads this to drive `fields` projection
  });

  it('call_unity_tool dispatches a Node-logic tool to its Node handler (not a raw passthrough)', async () => {
    let sent;
    const conn = fakeConn(async (type, p) => { sent = { type, p }; return { success: true }; });
    const h = new CallUnityToolToolHandler(fakeManager({ conn }));
    await h.execute({ instance: '7000', tool: 'execute_menu_item', params: { menuPath: 'GameObject/Create Empty' } });
    assert.equal(sent.type, 'execute_menu_item'); // the Node handler ran and forwarded after normalization
    assert.equal(sent.p.menuPath, 'GameObject/Create Empty');
  });
});

describe('Roslyn capability framework (Plan 2)', () => {
  const offRoslyn = () => ({ isReady: () => false, client: () => null, start: async () => 'unavailable', stop: async () => {}, statusOf: () => ({ state: 'off', error: null }) });
  const readyRoslyn = (client) => ({ isReady: () => true, client: () => client, start: async () => 'ready', stop: async () => {}, statusOf: () => ({ state: 'ready', error: null }) });

  it('call_unity_tool routes a gated Roslyn command to ROSLYN_NOT_READY when the backend is off', async () => {
    const h = new CallUnityToolToolHandler(fakeManager(), offRoslyn());
    const res = await h.handle({ instance: '7000', tool: 'rename_symbol', params: { path: 'A.cs', position: {}, newName: 'B' } });
    assert.equal(res.status, 'error');
    assert.match(res.error, /ROSLYN_NOT_READY|start_roslyn/);
  });

  it('call_unity_tool runs start_roslyn through the manager', async () => {
    let started = false;
    const roslyn = { ...offRoslyn(), start: async () => { started = true; return 'unavailable'; }, statusOf: () => ({ state: 'unavailable', error: 'Roslyn backend is not installed' }) };
    const h = new CallUnityToolToolHandler(fakeManager(), roslyn);
    const res = await h.handle({ instance: '7000', tool: 'start_roslyn' });
    assert.equal(started, true);
    assert.equal(res.status, 'success');
    assert.equal(res.result.state, 'unavailable');
  });

  it('call_unity_tool proxies a gated command to the sidecar when ready', async () => {
    const client = { call: async (tool, p) => ({ tool, p }) };
    const h = new CallUnityToolToolHandler(fakeManager(), readyRoslyn(client));
    const res = await h.handle({ instance: '7000', tool: 'rename_symbol', params: { path: 'A.cs', position: {}, newName: 'B' } });
    assert.equal(res.status, 'success');
    assert.equal(res.result.tool, 'rename_symbol');
  });

  it('list_unity_tools marks gated commands available when the backend is ready', async () => {
    const h = new ListUnityToolsToolHandler(fakeManager(), readyRoslyn({}));
    const r = await h.execute({ instance: '7000' });
    const rename = r.tools.find((t) => t.name === 'rename_symbol');
    assert.ok(rename && rename.requires === 'roslyn');
    assert.equal(rename.available, true);
  });
});
