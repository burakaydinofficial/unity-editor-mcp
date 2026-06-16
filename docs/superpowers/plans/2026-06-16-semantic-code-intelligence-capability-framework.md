# Semantic Code Intelligence — Capability Framework (0.6.0, Plan 2 of 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Node-side per-instance Roslyn capability framework — a `RoslynManager` state store, the `start_roslyn`/`stop_roslyn`/`roslyn_status` lifecycle, dynamic advertisement of the semantic commands via `list_unity_tools`, and the `call_unity_tool` routing — so the sidecar (Plan 3) plugs straight in. Until the sidecar exists, `start_roslyn` returns `unavailable` cleanly and the gated commands list as `available:false`.

**Architecture:** A module-singleton `RoslynManager` holds `Map<instanceKey, {state, client}>`. The lifecycle + gated commands live in a Node-side `roslynTools` registry (NOT the catalog). `list_unity_tools` merges them into the per-instance surface (always-listed, annotated `requires:"roslyn"` + `available`). `call_unity_tool` routes them: lifecycle ops drive the manager; gated commands proxy to the sidecar client when `ready`, else return `ROSLYN_NOT_READY`; `find_references` proxies to the sidecar when `ready` (semantic) and otherwise falls through to the editor (syntactic). Pure JS; no editor changes.

**Tech Stack:** Node.js ES modules; the existing `BaseToolHandler` / `UnityConnectionManager` / `nodeLogicTools` patterns; Node's built-in test runner.

---

## Design refinement vs. the spec (§3) — READ FIRST

The spec said: catalog the lifecycle + gated commands as `sides:["server"]` with Node handlers, "drift satisfied with zero gate changes." **That is infeasible** and this plan deviates deliberately:

- `protocol/scripts/lib/sources.mjs::getServerTools()` derives the drift gate's server set from **`createHandlers()`** — the exact same registry that feeds the MCP **ListTools** surface (`mcp-server/src/core/server.js`). So any command cataloged `sides:["server"]` must be a `createHandlers` entry, which would **advertise it as one of the top-level MCP tools** — breaking the v0.5.0 "exactly 3 meta-tools" surface (ADR 0006). `check-drift.mjs:145` also fails if a server handler is absent from `catalogServer`, so the two are locked together.
- **Refinement:** the Roslyn commands are inherently *dynamic and per-instance* (sidecar-dependent), so they belong in the **per-instance surface (`list_unity_tools`)**, not the static catalog. They live in a Node `roslynTools` registry, are advertised via `list_unity_tools` when the backend is available, and are routed by `call_unity_tool`. **The static catalog and the drift gate are untouched.** This matches the v0.5.0 model: catalog = always-present editor/server commands; `list_unity_tools` = what *this* instance can do right now.

---

## File structure

- **Create** `mcp-server/src/core/roslynManager.js` — the `RoslynManager` class + module singleton + an injectable client factory (default: no sidecar → `unavailable`).
- **Create** `mcp-server/src/core/roslynTools.js` — the lifecycle + gated command registry (names, descriptions, inputSchemas), the `isRoslyn*` predicates, `mergeRoslynSurface`, and `roslynDispatch`.
- **Modify** `mcp-server/src/handlers/instances/CallUnityToolToolHandler.js` — accept a `roslynMgr` dep; route Roslyn commands before the Node-logic/editor paths.
- **Modify** `mcp-server/src/handlers/instances/ListUnityToolsToolHandler.js` — accept a `roslynMgr` dep; merge the Roslyn surface.
- **Modify** `mcp-server/src/handlers/index.js` — wire the `roslynManager` singleton into the two meta-tool handlers.
- **Create** `mcp-server/tests/unit/core/roslynManager.test.js`, `mcp-server/tests/unit/core/roslynTools.test.js`; **modify** `mcp-server/tests/unit/handlers/instances/MetaTools.test.js`.
- **Modify** `mcp-server/package.json` — add the two new test files to `test:ci`.

**Instance key:** use the resolved connection's key (`conn.key`, i.e. `host:port`) so Roslyn state is per-editor, consistent with `UnityConnectionManager`.

---

## Task 1: `RoslynManager` — per-instance state machine

**Files:**
- Create: `mcp-server/src/core/roslynManager.js`
- Create: `mcp-server/tests/unit/core/roslynManager.test.js`

- [ ] **Step 1: Write the failing test**

Create `mcp-server/tests/unit/core/roslynManager.test.js`:

```js
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
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd mcp-server && node --test tests/unit/core/roslynManager.test.js`
Expected: FAIL — `Cannot find module '../../../src/core/roslynManager.js'`.

- [ ] **Step 3: Write the implementation**

Create `mcp-server/src/core/roslynManager.js`:

```js
/**
 * Per-instance Roslyn sidecar state (ADR 0006 capability handshake). The sidecar process itself is
 * Plan 3; this holds the state machine and an injectable client factory. The DEFAULT factory produces
 * no client (returns null) → start() resolves to 'unavailable', so the framework ships inert until the
 * sidecar backend is installed. Keyed by the resolved connection key (host:port) — Roslyn state is
 * per-editor.
 */
export const ROSLYN_STATES = Object.freeze({ OFF: 'off', INDEXING: 'indexing', READY: 'ready', UNAVAILABLE: 'unavailable' });

// Default: no sidecar backend present. Plan 3 swaps in a factory that spawns + connects the .NET process.
async function defaultClientFactory() { return null; }

export class RoslynManager {
  constructor(clientFactory = defaultClientFactory) {
    this._byKey = new Map(); // key -> { state, client, error }
    this._clientFactory = clientFactory;
  }

  getState(key) { return this._byKey.get(key)?.state ?? ROSLYN_STATES.OFF; }
  isReady(key) { return this.getState(key) === ROSLYN_STATES.READY; }
  client(key) { return this._byKey.get(key)?.client ?? null; }
  statusOf(key) {
    const e = this._byKey.get(key);
    return { state: e?.state ?? ROSLYN_STATES.OFF, error: e?.error ?? null };
  }

  async start(key, conn) {
    const existing = this._byKey.get(key);
    if (existing && (existing.state === ROSLYN_STATES.READY || existing.state === ROSLYN_STATES.INDEXING)) {
      return existing.state; // idempotent
    }
    this._byKey.set(key, { state: ROSLYN_STATES.INDEXING, client: null });
    try {
      const client = await this._clientFactory(conn);
      if (!client) {
        this._byKey.set(key, { state: ROSLYN_STATES.UNAVAILABLE, client: null, error: 'Roslyn backend is not installed' });
        return ROSLYN_STATES.UNAVAILABLE;
      }
      this._byKey.set(key, { state: ROSLYN_STATES.READY, client });
      return ROSLYN_STATES.READY;
    } catch (e) {
      this._byKey.set(key, { state: ROSLYN_STATES.UNAVAILABLE, client: null, error: e.message });
      return ROSLYN_STATES.UNAVAILABLE;
    }
  }

  async stop(key) {
    const e = this._byKey.get(key);
    if (e?.client?.dispose) { try { await e.client.dispose(); } catch { /* ignore */ } }
    this._byKey.delete(key);
  }
}

// Module singleton the meta-tool handlers use in production (default factory → inert until Plan 3).
export const roslynManager = new RoslynManager();
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd mcp-server && node --test tests/unit/core/roslynManager.test.js`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add mcp-server/src/core/roslynManager.js mcp-server/tests/unit/core/roslynManager.test.js
git commit -m "feat(0.6.0): RoslynManager — per-instance sidecar state (Plan 2)"
```

---

## Task 2: `roslynTools` registry, surface merge, and dispatch

**Files:**
- Create: `mcp-server/src/core/roslynTools.js`
- Create: `mcp-server/tests/unit/core/roslynTools.test.js`

- [ ] **Step 1: Write the failing test**

Create `mcp-server/tests/unit/core/roslynTools.test.js`:

```js
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
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd mcp-server && node --test tests/unit/core/roslynTools.test.js`
Expected: FAIL — module not found.

- [ ] **Step 3: Write the implementation**

Create `mcp-server/src/core/roslynTools.js`:

```js
/**
 * The Roslyn command registry + routing (ADR 0006 capability handshake). These commands are NOT in the
 * static protocol catalog — they are dynamic, per-instance, sidecar-dependent (see the Plan 2 design
 * refinement). list_unity_tools advertises them per instance; call_unity_tool routes them here.
 */

/** Sentinel: this tool is not a Roslyn command (or find_references with no sidecar) — let the caller fall through. */
export const NOT_HANDLED = Symbol('roslyn:not-handled');

const objSchema = (props = {}, required = []) => ({ type: 'object', properties: props, required });

// Lifecycle — always available (they manage the backend itself).
export const ROSLYN_LIFECYCLE = {
  start_roslyn: {
    description: 'Activate the Roslyn semantic backend for this instance (spawns the sidecar; async — poll roslyn_status). Returns "unavailable" if the backend is not installed.',
    inputSchema: objSchema(),
  },
  stop_roslyn: { description: 'Tear down the Roslyn sidecar for this instance.', inputSchema: objSchema() },
  roslyn_status: { description: 'Report the Roslyn backend state for this instance (off | indexing | ready | unavailable).', inputSchema: objSchema() },
};

// Gated — advertised always (so the agent learns they exist + that start_roslyn unlocks them) but only
// invokable when ready. Params mirror the spec's command surface (§4).
export const ROSLYN_GATED = {
  goto_definition: {
    description: 'Semantic go-to-definition (overload-resolved). Requires the Roslyn backend (start_roslyn).',
    inputSchema: objSchema({ path: { type: 'string' }, position: { type: 'object' } }, ['path', 'position']),
  },
  rename_symbol: {
    description: 'Cross-file safe rename. dryRun returns the edit set without writing. Requires the Roslyn backend.',
    inputSchema: objSchema({ path: { type: 'string' }, position: { type: 'object' }, newName: { type: 'string' }, dryRun: { type: 'boolean' } }, ['path', 'position', 'newName']),
  },
  get_diagnostics: {
    description: 'Compiler errors/warnings for a file or the whole compilation. Requires the Roslyn backend.',
    inputSchema: objSchema({ path: { type: 'string' } }),
  },
  get_type_hierarchy: {
    description: 'Base / derived / implemented types across the compilation. Requires the Roslyn backend.',
    inputSchema: objSchema({ typeName: { type: 'string' } }, ['typeName']),
  },
};

export const isRoslynLifecycle = (name) => Object.prototype.hasOwnProperty.call(ROSLYN_LIFECYCLE, name);
export const isRoslynGated = (name) => Object.prototype.hasOwnProperty.call(ROSLYN_GATED, name);
/** True for commands roslynTools OWNS outright (lifecycle + gated). find_references is editor-owned + handled specially. */
export const isRoslynCommand = (name) => isRoslynLifecycle(name) || isRoslynGated(name);

/** Append the Roslyn commands to a per-instance tool surface, annotated requires/available. */
export function mergeRoslynSurface(surface, instanceKey, roslynMgr) {
  const ready = roslynMgr.isReady(instanceKey);
  const lifecycle = Object.entries(ROSLYN_LIFECYCLE).map(([name, d]) => ({
    name, category: 'roslyn', description: d.description, params: d.inputSchema, available: true,
  }));
  const gated = Object.entries(ROSLYN_GATED).map(([name, d]) => ({
    name, category: 'roslyn', description: d.description, params: d.inputSchema, requires: 'roslyn', available: ready,
  }));
  return [...surface, ...lifecycle, ...gated];
}

/**
 * Route a tool through the Roslyn layer. Returns NOT_HANDLED for anything this layer does not own
 * (so call_unity_tool continues to its Node-logic/editor paths). `conn` carries the resolved connection.
 */
export async function roslynDispatch(tool, params, conn, instanceKey, roslynMgr) {
  if (isRoslynLifecycle(tool)) {
    if (tool === 'start_roslyn') {
      const state = await roslynMgr.start(instanceKey, conn);
      return { instance: instanceKey, state, ...(roslynMgr.statusOf(instanceKey).error ? { error: roslynMgr.statusOf(instanceKey).error } : {}) };
    }
    if (tool === 'stop_roslyn') { await roslynMgr.stop(instanceKey); return { instance: instanceKey, state: 'off' }; }
    return { instance: instanceKey, ...roslynMgr.statusOf(instanceKey) }; // roslyn_status
  }
  if (isRoslynGated(tool)) {
    if (!roslynMgr.isReady(instanceKey)) {
      const err = new Error(`Roslyn backend not ready for this instance — call start_roslyn first (ROSLYN_NOT_READY).`);
      err.code = 'ROSLYN_NOT_READY';
      throw err;
    }
    return await roslynMgr.client(instanceKey).call(tool, params);
  }
  if (tool === 'find_references' && roslynMgr.isReady(instanceKey)) {
    return await roslynMgr.client(instanceKey).call('find_references', params); // semantic upgrade
  }
  return NOT_HANDLED; // not ours (or find_references with no sidecar → editor syntactic)
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd mcp-server && node --test tests/unit/core/roslynTools.test.js`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add mcp-server/src/core/roslynTools.js mcp-server/tests/unit/core/roslynTools.test.js
git commit -m "feat(0.6.0): roslynTools registry — surface merge + dispatch (Plan 2)"
```

---

## Task 3: Route Roslyn commands in `call_unity_tool`

**Files:**
- Modify: `mcp-server/src/handlers/instances/CallUnityToolToolHandler.js`
- Modify: `mcp-server/tests/unit/handlers/instances/MetaTools.test.js`

- [ ] **Step 1: Write the failing test**

Add to `MetaTools.test.js` (inside the `call_unity_tool` describe block; reuse the file's existing `fakeManager`/`fakeConn` helpers). A fake roslyn manager is passed as the 2nd ctor arg:

```js
it('call_unity_tool routes a gated Roslyn command to ROSLYN_NOT_READY when the backend is off', async () => {
  const offRoslyn = { isReady: () => false, client: () => null, start: async () => 'unavailable', stop: async () => {}, statusOf: () => ({ state: 'off', error: null }) };
  const h = new CallUnityToolToolHandler(fakeManager({ conn: degradedConn() }), offRoslyn);
  const res = await h.handle({ instance: '7000', tool: 'rename_symbol', params: { path: 'A.cs', position: {}, newName: 'B' } });
  assert.equal(res.status, 'error');
  assert.match(res.error, /ROSLYN_NOT_READY|start_roslyn/);
});

it('call_unity_tool runs start_roslyn through the manager', async () => {
  let started = false;
  const roslyn = { isReady: () => false, client: () => null, start: async () => { started = true; return 'unavailable'; }, stop: async () => {}, statusOf: () => ({ state: 'unavailable', error: 'Roslyn backend is not installed' }) };
  const h = new CallUnityToolToolHandler(fakeManager({ conn: degradedConn() }), roslyn);
  const res = await h.handle({ instance: '7000', tool: 'start_roslyn' });
  assert.equal(started, true);
  assert.equal(res.status, 'success');
  assert.equal(res.result.state, 'unavailable');
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd mcp-server && node --test tests/unit/handlers/instances/MetaTools.test.js`
Expected: FAIL — the handler ignores the 2nd arg and forwards `rename_symbol` to the editor (or rejects as unavailable), not `ROSLYN_NOT_READY`.

- [ ] **Step 3: Implement the routing**

In `CallUnityToolToolHandler.js`, add the import at the top:

```js
import { roslynManager } from '../../core/roslynManager.js';
import { roslynDispatch, NOT_HANDLED } from '../../core/roslynTools.js';
```

Change the constructor to accept and store the roslyn manager (default = the singleton):

```js
  constructor(manager, roslynMgr = roslynManager) {
    super(/* …existing super(...) call unchanged… */);
    this.manager = manager;
    this.roslynMgr = roslynMgr;
  }
```

In `execute()`, after `const callParams = …;` and its object guard, **before** the Node-logic / editor-passthrough branches, insert the Roslyn routing (use the resolved connection key as the instance key):

```js
    const instanceKey = conn.key ?? String(params.instance);
    const roslynResult = await roslynDispatch(params.tool, callParams, conn, instanceKey, this.roslynMgr);
    if (roslynResult !== NOT_HANDLED) return roslynResult;
```

(`find_references` with no sidecar returns `NOT_HANDLED` and continues to the existing editor passthrough → syntactic, exactly as today.)

- [ ] **Step 4: Run it to verify it passes**

Run: `cd mcp-server && node --test tests/unit/handlers/instances/MetaTools.test.js`
Expected: PASS (the new tests + all existing).

- [ ] **Step 5: Commit**

```bash
git add mcp-server/src/handlers/instances/CallUnityToolToolHandler.js mcp-server/tests/unit/handlers/instances/MetaTools.test.js
git commit -m "feat(0.6.0): call_unity_tool routes Roslyn lifecycle + gated commands (Plan 2)"
```

---

## Task 4: Advertise the Roslyn surface in `list_unity_tools`

**Files:**
- Modify: `mcp-server/src/handlers/instances/ListUnityToolsToolHandler.js`
- Modify: `mcp-server/tests/unit/handlers/instances/MetaTools.test.js`

- [ ] **Step 1: Write the failing test**

Add to `MetaTools.test.js`:

```js
it('list_unity_tools advertises the gated Roslyn commands with available=false when the backend is off', async () => {
  const offRoslyn = { isReady: () => false, client: () => null, start: async () => 'unavailable', stop: async () => {}, statusOf: () => ({ state: 'off', error: null }) };
  const conn = { editorInfo: { commands: [{ name: 'ping', category: 'system', description: 'p', params: { type: 'object' } }] }, isConnected: () => true, sendCommand: async () => ({}) };
  const h = new ListUnityToolsToolHandler(fakeManager({ conn }), offRoslyn);
  const r = await h.execute({ instance: '7000' });
  const rename = r.tools.find((t) => t.name === 'rename_symbol');
  assert.ok(rename, 'rename_symbol must be advertised even when off');
  const start = r.tools.find((t) => t.name === 'start_roslyn');
  assert.ok(start, 'start_roslyn must be advertised');
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd mcp-server && node --test tests/unit/handlers/instances/MetaTools.test.js`
Expected: FAIL — `rename_symbol` is not in the surface (and the handler ignores the 2nd arg).

- [ ] **Step 3: Implement the merge**

In `ListUnityToolsToolHandler.js`, add the imports:

```js
import { roslynManager } from '../../core/roslynManager.js';
import { mergeRoslynSurface } from '../../core/roslynTools.js';
```

Accept + store the roslyn manager (default = singleton):

```js
  constructor(manager, roslynMgr = roslynManager) {
    super(/* …existing super(...) call unchanged… */);
    this.manager = manager;
    this.roslynMgr = roslynMgr;
  }
```

In `execute()`, after `const surface = mergeNodeLogicSurface(raw);`, add the Roslyn merge (keyed by the resolved connection):

```js
    const instanceKey = conn.key ?? String(params.instance);
    const fullSurface = mergeRoslynSurface(surface, instanceKey, this.roslynMgr);
```

Then use `fullSurface` instead of `surface` in BOTH the single-tool (`name`) branch and the list branch. In the list branch, include the `requires`/`available` annotations so the agent sees them:

```js
    if (params.name) {
      const tool = fullSurface.find((t) => t.name === params.name);
      if (!tool) throw new Error(`Tool "${params.name}" is not available on this instance. Use list_unity_tools to see what is.`);
      return { instance: params.instance ?? null, tool, schemasAvailable: hasSchemas };
    }
    let tools = fullSurface;
    if (params.category) tools = tools.filter((t) => t.category === params.category);
    return {
      instance: params.instance ?? null,
      count: tools.length,
      tools: tools.map((t) => ({
        name: t.name, category: t.category ?? null, description: t.description ?? '',
        ...(t.requires ? { requires: t.requires } : {}),
        ...(t.available === false ? { available: false } : {}),
      })),
      schemasAvailable: hasSchemas,
    };
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd mcp-server && node --test tests/unit/handlers/instances/MetaTools.test.js`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add mcp-server/src/handlers/instances/ListUnityToolsToolHandler.js mcp-server/tests/unit/handlers/instances/MetaTools.test.js
git commit -m "feat(0.6.0): list_unity_tools advertises the Roslyn capability surface (Plan 2)"
```

---

## Task 5: Wire the singleton + gate the new tests

**Files:**
- Modify: `mcp-server/src/handlers/index.js`
- Modify: `mcp-server/package.json`

- [ ] **Step 1: Confirm `createHandlers` wires the singleton**

`CallUnityToolToolHandler` and `ListUnityToolsToolHandler` now default their 2nd ctor arg to the `roslynManager` singleton, so `createHandlers(manager)` (in `handlers/index.js`) already passes the singleton implicitly — no change needed unless `index.js` constructs them with extra args. Read `mcp-server/src/handlers/index.js` and verify each is built as `new HandlerClass(manager)`. If so, leave it; if it passes more, ensure the roslyn arg is not clobbered.

- [ ] **Step 2: Add the new unit tests to `test:ci`**

In `mcp-server/package.json`, append to the `test:ci` file list (before the closing quote):
` tests/unit/core/roslynManager.test.js tests/unit/core/roslynTools.test.js`

- [ ] **Step 3: Run the gates**

Run:
```bash
cd mcp-server && npm run test:ci 2>&1 | grep -E "ℹ (pass|fail) "
cd .. && node protocol/scripts/check-drift.mjs
```
Expected: test:ci all pass (incl. the new files + the MetaTools additions); drift OK and **unchanged** (catalog 84 / server 3 / editor 81 — the Roslyn commands are not cataloged, by design).

- [ ] **Step 4: Full unit suite (regression)**

Run: `cd mcp-server && npx --no-install c8 --reporter=text-summary node --test 'tests/unit/**/*.test.js' 2>&1 | grep -E "ℹ (pass|fail) "`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add mcp-server/package.json mcp-server/src/handlers/index.js
git commit -m "chore(0.6.0): gate the Roslyn framework tests; wire the manager singleton (Plan 2)"
```

---

## Self-review

- **Spec coverage:** Implements §2 (Layer 2 capability gate), §3 (the dynamic surface mechanism — via the documented refinement: a Node registry + `list_unity_tools`, not the catalog, because `getServerTools` couples server-catalog to the MCP surface), §4 (the lifecycle + gated command set), and §5 (lifecycle state machine, `unavailable` when the backend is absent). The **sidecar process + its real client factory + the editor model export** are Plan 3 — here the default factory yields `unavailable` and the gated path yields `ROSLYN_NOT_READY`, both tested.
- **Type consistency:** `RoslynManager` methods (`getState`/`isReady`/`client`/`statusOf`/`start`/`stop`) are used identically by `roslynTools` and the handler tests; `mergeRoslynSurface(surface, instanceKey, roslynMgr)` and `roslynDispatch(tool, params, conn, instanceKey, roslynMgr)` signatures match every call site; the `NOT_HANDLED` sentinel is imported wherever compared.
- **No placeholders:** every step has the exact file, real test, real implementation, and the exact command + expected result.
- **Drift safety (the key risk):** verified by Step 3 of Task 5 — the catalog and `check-drift.mjs` are untouched, so the counts stay 84/3/81. The Roslyn commands never enter `getServerTools`, so the MCP ListTools surface stays at exactly 3 meta-tools.
- **Assumption to verify at execution:** `conn.key` is the connection's `host:port` identifier (read `UnityConnectionManager`/`UnityConnection` to confirm the property name; if it differs, use the actual key getter). The fallback `String(params.instance)` keeps it correct even if `conn.key` is absent.
