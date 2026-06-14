# list_unity_instances Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only `list_unity_instances` MCP tool that reports the Unity editors currently discoverable in the per-user registry — the v1 down-payment on ADR 0004's capability-driven surface and the first piece of first-class multi-instance support.

**Architecture:** A new local-only handler (`ListUnityInstancesToolHandler`) that reads the filesystem instance registry through the existing `mcp-server/src/core/discovery.js` functions — it never touches the Unity wire, so it works with no editor connected. Discovery functions are injected via the constructor (default = the real module) so the handler is unit-tested deterministically without a filesystem or a live editor, mirroring how existing handlers inject a mock `unityConnection`. It is registered like any other tool and added to the protocol catalog as the repo's first `sides: ["server"]` command (no editor/C# side).

**Tech Stack:** Node ≥18 ESM, `node:test` + `node:assert/strict`, the protocol drift gate (`protocol/scripts/check-drift.mjs`).

---

## File Structure

- `mcp-server/src/handlers/instances/ListUnityInstancesToolHandler.js` (Create) — the handler; sole responsibility is shaping the registry into an agent-facing instance list.
- `mcp-server/tests/unit/handlers/instances/ListUnityInstancesToolHandler.test.js` (Create) — unit tests (injected fake discovery deps).
- `mcp-server/src/handlers/index.js` (Modify) — register the handler (export + import + `HANDLER_CLASSES` entry).
- `protocol/catalog/commands.json` (Modify) — add the `list_unity_instances` command, `sides: ["server"]`, `params` == handler `inputSchema`.
- `mcp-server/tests/unit/core/server.test.js:138` (Modify) — bump `handlers.size` 66 → 67.
- `mcp-server/package.json` (Modify) — add the new test file to `test:ci`.
- `README.md:83` (Modify) — tool count 66→67, 12→13 categories, add an Instance Management section.

No C# changes: `generate-csharp-catalog.mjs` emits only `editor`-side commands, and the drift gate handles a server-only command via its `catalogServer` set.

---

### Task 1: The handler (TDD)

**Files:**
- Create: `mcp-server/src/handlers/instances/ListUnityInstancesToolHandler.js`
- Test: `mcp-server/tests/unit/handlers/instances/ListUnityInstancesToolHandler.test.js`

- [ ] **Step 1: Write the failing test**

Create `mcp-server/tests/unit/handlers/instances/ListUnityInstancesToolHandler.test.js`:

```js
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
    const h = new ListUnityInstancesToolHandler({}, fakeDeps([]));
    assert.equal(h.name, 'list_unity_instances');
    assert.ok(h.description.length > 0);
    assert.deepEqual(h.inputSchema.required, []);
    assert.equal(h.inputSchema.properties.includeStale.type, 'boolean');
  });

  it('returns only live instances by default', async () => {
    const h = new ListUnityInstancesToolHandler({}, fakeDeps([
      inst({ projectPath: 'C:/proj/A', __live: true }),
      inst({ projectPath: 'C:/proj/B', __live: false }),
    ]));
    const r = await h.execute({});
    assert.equal(r.count, 1);
    assert.equal(r.instances[0].projectPath, 'C:/proj/A');
    assert.equal(r.instances[0].live, true);
  });

  it('includes stale descriptors when includeStale=true', async () => {
    const h = new ListUnityInstancesToolHandler({}, fakeDeps([
      inst({ projectPath: 'C:/proj/A', __live: true }),
      inst({ projectPath: 'C:/proj/B', __live: false }),
    ]));
    const r = await h.execute({ includeStale: true });
    assert.equal(r.count, 2);
    assert.equal(r.instances.find((i) => i.projectPath === 'C:/proj/B').live, false);
  });

  it('marks the active instance by resolved port', async () => {
    const h = new ListUnityInstancesToolHandler({}, fakeDeps([
      inst({ projectPath: 'C:/proj/A', port: 6500, __live: true }),
      inst({ projectPath: 'C:/proj/B', port: 6600, __live: true }),
    ], 6600));
    const r = await h.execute({});
    assert.equal(r.activePort, 6600);
    assert.equal(r.instances.find((i) => i.port === 6600).active, true);
    assert.equal(r.instances.find((i) => i.port === 6500).active, false);
  });

  it('sorts by projectPath and exposes only known fields', async () => {
    const h = new ListUnityInstancesToolHandler({}, fakeDeps([
      inst({ projectPath: 'C:/proj/Z', __live: true }),
      inst({ projectPath: 'C:/proj/A', __live: true }),
    ]));
    const r = await h.execute({});
    assert.deepEqual(r.instances.map((i) => i.projectPath), ['C:/proj/A', 'C:/proj/Z']);
    assert.ok(!('__live' in r.instances[0]));
  });

  it('empty registry yields an empty list and reports the registry dir', async () => {
    const h = new ListUnityInstancesToolHandler({}, fakeDeps([]));
    const r = await h.execute({});
    assert.equal(r.count, 0);
    assert.deepEqual(r.instances, []);
    assert.equal(r.registryDir, '/fake/registry');
  });

  it('a throwing resolveUnityPort does not break listing', async () => {
    const deps = fakeDeps([inst({ __live: true })]);
    deps.resolveUnityPort = () => { throw new Error('boom'); };
    const h = new ListUnityInstancesToolHandler({}, deps);
    const r = await h.execute({});
    assert.equal(r.count, 1);
    assert.equal(r.activePort, null);
  });

  it('handle() wraps the result in the success envelope', async () => {
    const h = new ListUnityInstancesToolHandler({}, fakeDeps([inst()]));
    const res = await h.handle({});
    assert.equal(res.status, 'success');
    assert.equal(res.result.count, 1);
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd mcp-server && node --test tests/unit/handlers/instances/ListUnityInstancesToolHandler.test.js`
Expected: FAIL — cannot find module `ListUnityInstancesToolHandler.js`.

- [ ] **Step 3: Write the handler**

Create `mcp-server/src/handlers/instances/ListUnityInstancesToolHandler.js`:

```js
import { BaseToolHandler } from '../base/BaseToolHandler.js';
import * as discovery from '../../core/discovery.js';

/**
 * Lists the Unity editor instances discoverable in the per-user registry
 * (ADR 0003 / 0004). LOCAL-ONLY: it reads the filesystem registry, never the
 * wire — the one tool that works with no editor connected, and the foundation of
 * the capability-driven surface (the agent picks an instance, then targets it).
 *
 * The discovery functions are injected (default = the real module) so the handler
 * is unit-tested without a filesystem or a live editor — the same dependency-
 * injection idiom the other handlers use for `unityConnection`.
 */
export class ListUnityInstancesToolHandler extends BaseToolHandler {
  constructor(unityConnection, deps = discovery) {
    super(
      'list_unity_instances',
      'List the Unity editor instances currently running and discoverable (project path, Unity version, port, and which one this server targets by default). Use this to see what editors are available before acting; works even when no editor is connected.',
      {
        type: 'object',
        properties: {
          includeStale: {
            type: 'boolean',
            description: 'Also include descriptors whose process is gone / heartbeat is stale (for diagnosing a missing editor). Default: false.',
          },
        },
        required: [],
      },
    );
    this.unityConnection = unityConnection;
    this.deps = deps;
  }

  async execute(params = {}) {
    const env = process.env;
    const registryDir = this.deps.defaultRegistryDirectory(env);
    const all = this.deps.readInstances(registryDir);

    let activePort = null;
    try {
      activePort = this.deps.resolveUnityPort(env);
    } catch {
      activePort = null; // discovery should never break a read-only listing
    }

    const includeStale = params.includeStale === true;
    const instances = all
      .map((d) => ({ d, live: this.deps.isLive(d) }))
      .filter((r) => includeStale || r.live)
      .map(({ d, live }) => ({
        projectPath: d.projectPath,
        unityVersion: d.unityVersion ?? null,
        port: Number.isFinite(d.port) ? d.port : null,
        pid: Number.isFinite(d.pid) ? d.pid : null,
        protocolVersion: d.protocolVersion ?? null,
        host: d.host ?? null,
        startedAt: d.startedAt ?? null,
        lastHeartbeat: d.lastHeartbeat ?? null,
        live,
        active: Number.isFinite(d.port) && activePort != null && d.port === activePort,
      }))
      .sort((a, b) => String(a.projectPath).localeCompare(String(b.projectPath)));

    return { instances, count: instances.length, registryDir, activePort };
  }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd mcp-server && node --test tests/unit/handlers/instances/ListUnityInstancesToolHandler.test.js`
Expected: PASS — 8 tests.

- [ ] **Step 5: Commit**

```bash
git add mcp-server/src/handlers/instances/ListUnityInstancesToolHandler.js mcp-server/tests/unit/handlers/instances/ListUnityInstancesToolHandler.test.js
git commit -m "feat(instances): list_unity_instances handler (read-only registry listing)"
```

---

### Task 2: Register the handler

**Files:**
- Modify: `mcp-server/src/handlers/index.js`

- [ ] **Step 1: Add the export** — after the test-runner export block (after line 102), add:

```js
// Instance handlers
export { ListUnityInstancesToolHandler } from './instances/ListUnityInstancesToolHandler.js';
```

- [ ] **Step 2: Add the import** — after the `CancelTestsToolHandler` import (line 170), add:

```js
import { ListUnityInstancesToolHandler } from './instances/ListUnityInstancesToolHandler.js';
```

- [ ] **Step 3: Add to `HANDLER_CLASSES`** — before the closing `]` of the array (after `CancelTestsToolHandler` on line 268), add a new group:

```js
,

  // Instance handlers
  ListUnityInstancesToolHandler
```

(Ensure the `CancelTestsToolHandler` line keeps/gets its trailing comma so the array stays valid.)

- [ ] **Step 4: Verify it registers** — Run: `cd mcp-server && node -e "import('./src/handlers/index.js').then(m => { const h = m.createHandlers({isConnected:()=>false,connect:async()=>{},sendCommand:async()=>({})}); console.log('size', h.size, 'has', h.has('list_unity_instances')); })"`
Expected: `size 67 has true`

- [ ] **Step 5: Commit**

```bash
git add mcp-server/src/handlers/index.js
git commit -m "feat(instances): register list_unity_instances in the handler registry"
```

---

### Task 3: Add to the protocol catalog (drift gate)

**Files:**
- Modify: `protocol/catalog/commands.json`

- [ ] **Step 1: Add the command entry** to the `commands` array. `params` MUST be byte-identical to the handler's `inputSchema` (the drift gate compares them canonically). Add:

```json
{
  "name": "list_unity_instances",
  "category": "instances",
  "sides": ["server"],
  "internal": false,
  "destructive": false,
  "description": "List the Unity editor instances currently running and discoverable (project path, Unity version, port, and which one this server targets by default). Use this to see what editors are available before acting; works even when no editor is connected.",
  "params": {
    "type": "object",
    "properties": {
      "includeStale": {
        "type": "boolean",
        "description": "Also include descriptors whose process is gone / heartbeat is stale (for diagnosing a missing editor). Default: false."
      }
    },
    "required": []
  }
}
```

- [ ] **Step 2: Run the drift gate** — Run: `node protocol/scripts/check-drift.mjs`
Expected: `protocol 1.0.0: OK — catalog 69 commands, server 67 tools, editor … commands, no new drift`. If it reports "Param schema drift for list_unity_instances", reconcile the catalog `params` to exactly match the handler `inputSchema` and re-run.

- [ ] **Step 3: Confirm the generated C# is unchanged** — Run: `node protocol/scripts/generate-csharp-catalog.mjs && git status --porcelain unity-editor-mcp/Core/CommandCatalog.g.cs`
Expected: no output (server-only command is not emitted to C#).

- [ ] **Step 4: Commit**

```bash
git add protocol/catalog/commands.json
git commit -m "protocol(catalog): declare list_unity_instances (first server-only command)"
```

---

### Task 4: Fix the hardcoded tool count

**Files:**
- Modify: `mcp-server/tests/unit/core/server.test.js:138`

- [ ] **Step 1: Update the assertion** — change line 138 from:

```js
      assert.equal(handlers.size, 66);
```

to:

```js
      assert.equal(handlers.size, 67);
```

- [ ] **Step 2: Run the server tests** — Run: `cd mcp-server && node --test tests/unit/core/server.test.js`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add mcp-server/tests/unit/core/server.test.js
git commit -m "test(server): handler count 66 -> 67 for list_unity_instances"
```

---

### Task 5: Wire into CI + docs

**Files:**
- Modify: `mcp-server/package.json` (the `test:ci` script)
- Modify: `README.md:83` (+ the Available Tools section)

- [ ] **Step 1: Add the test to `test:ci`** — in `mcp-server/package.json`, append the new test file to the `test:ci` node `--test` file list (before the closing quote):

```
 tests/unit/handlers/instances/ListUnityInstancesToolHandler.test.js
```

- [ ] **Step 2: Update the README count + add a section** — change `README.md:83` from `**66 comprehensive tools** across 12 categories` to `**67 comprehensive tools** across 13 categories`, and add a new section (placement: right after the System & Core block):

```markdown
### Instance Management (1 tool)
- **`list_unity_instances`** - List the Unity editors currently running and discoverable (project, version, port, active target); works with no editor connected
```

- [ ] **Step 3: Run the CI test set** — Run: `cd mcp-server && npm run test:ci`
Expected: PASS, includes the new test, exits cleanly.

- [ ] **Step 4: Commit**

```bash
git add mcp-server/package.json README.md
git commit -m "ci+docs: cover list_unity_instances in test:ci; document the tool (67 tools)"
```

---

### Task 6: Full-gate verification

- [ ] **Step 1: Run every gate.**

```bash
node protocol/scripts/check-drift.mjs
node scripts/compat-lint.mjs
cd mcp-server && npm run test:ci
cd ../dotnet/UnityEditorMCP.Core.Tests && dotnet test
```

Expected: drift OK (67 server tools), compat-lint OK, Node CI green (incl. the new test), dotnet 103/103 (unchanged — no C# touched).

- [ ] **Step 2: Confirm tree is clean and review the diff.**

Run: `git status --porcelain` (expect clean) and `git diff main --stat`.

---

## Future phases (separate plans — ADR 0004)

These are the rest of the capability-driven surface, deferred to 1.x and each warranting its own spec+plan when scheduled (do not stub here):

1. **Handshake schema advertisement** — extend `Handshake.cs` / `handshake.js` to carry full param schemas (today: names only); cache per instance keyed by a digest.
2. **`call_unity_tool` + Node-side JSON-Schema validator** — generic dispatch with precise field-level validation against the cached schema (pure-JS validator decision).
3. **`list_unity_tools`** — lazy capability discovery (names+summaries, schema on demand).
4. **Typed-exposure opt-in flag** — re-expose the catalog as native typed handlers for capable setups.
5. **Make generic the default surface** — flip the canonical surface once 1–4 land.

---

## Self-Review

- **Spec coverage (vs ADR 0004):** This plan implements exactly the ADR's stated v1 down-payment — `list_unity_instances`, read-only, on `discovery.js` — and nothing more; the remaining ADR phases are explicitly listed as future separate plans. ✓
- **Placeholder scan:** No TBD/TODO; every code/edit step shows the actual content; exact commands with expected output. ✓
- **Type/name consistency:** `ListUnityInstancesToolHandler`, tool name `list_unity_instances`, fields `{ instances, count, registryDir, activePort }`, and per-instance fields are identical across the handler, the tests, and the catalog `params`. The catalog `params` is byte-identical to the handler `inputSchema` (required by the drift gate). Counts: 66→67 server tools, catalog grows by one (drift output will read 69 commands = prior 68 + 1). ✓
