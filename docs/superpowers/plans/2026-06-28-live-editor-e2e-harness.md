# Live-Editor E2E Harness — Implementation Plan

> **STATUS: COMPLETE (2026-07-02).** All 8 tasks implemented, agent-reviewed, and verified live on 2022.3.62f2
> (selfcheck + playmode + scripts + reconnect-stability pass; see `mcp-server/tests/e2e/live/`). Deviations from this
> plan, discovered during execution: `call_unity_tool` args are `{ instance, tool, params }` (not `{toolName,parameters}`);
> `get_editor_state` nests its payload under `state`; the compile signal is `parseCompile` on the `-logFile` (NOT
> `get_compilation_state.errorCount`, which is monitoring-based and resets on reload); `pause_game` is a toggle and is
> driven via the driver's `{ once }` no-retry path; the harness also surfaced + fixed a real bridge reload-recovery bug
> (ADR 0007). This document is retained as the historical plan.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a local-first, bridge-driven E2E harness that gives deterministic, outcome-verified coverage to the
play-mode and script/recompile tools that cannot run in the EditMode floor-matrix.

**Architecture:** A Node runner launches one headed Unity editor on a persistent warm `ci/e2e-host` project, drives
tools through the real MCP client→server→editor chain, and verifies each effect with a mixed model (bridge read-back +
version-robust independent channels: filesystem, `error CS` compile grammar, and an in-editor probe's own JSON). All
tool-induced domain reloads happen inside the one editor; the harness retries tool calls across each reload.

**Tech Stack:** Node ≥18 pure ESM (`@modelcontextprotocol/sdk`), Unity C# 7.3 / netstandard 2.0 (the probe), the
existing `unity-editor-mcp` package + `mcp-server`.

**Design of record:** `docs/superpowers/specs/2026-06-28-live-editor-e2e-harness-design.md`.

## Global Constraints

- Node harness stays **pure JS, no native modules**, ESM (`"type": "module"`), Node ≥18. Only runtime dep is
  `@modelcontextprotocol/sdk` (already present).
- The in-editor probe is **C# 7.3 / netstandard 2.0**, IMGUI-safe, no UI Toolkit — it must compile on the 2019.4 floor.
- Independent verification uses **only version-robust signals**: file existence, the stable `error CS` compiler
  grammar, and the probe's own JSON. **Never** parse `.unity`/`.prefab`/`.asset` YAML or exact log prose.
- The MCP server entry is `mcp-server/src/core/server.js`. **All server logging is stderr**; stdout is MCP JSON-RPC.
- This harness is **not** wired into `.github/workflows/floor-matrix.yml`. New `test:e2e:live` script only; `npm test`
  and the floor-matrix are untouched. `ci/e2e-host/` deliberately uses a prefix outside the floor-matrix trigger glob
  `ci/unity-host-**`.
- Pure-Node units are unit-tested under `mcp-server/tests/unit/e2e-live/` (run by the default `test`/`test:unit`);
  the live harness lives under `mcp-server/tests/e2e/live/` and runs **only** via `test:e2e:live`.

## File Structure

**Create:**
- `ci/e2e-host/Packages/manifest.json` — minimal host, package via `file:` ref (no `testables`).
- `ci/e2e-host/Assets/Editor/E2EProbe.cs` (+ `.meta`) — the in-editor probe.
- `ci/e2e-host/.gitignore` — ignore generated `ProjectSettings` churn is NOT wanted; see Task 4.
- `mcp-server/tests/e2e/live/verify.mjs` — verify helpers.
- `mcp-server/tests/e2e/live/retry.mjs` — retry-across-transient util.
- `mcp-server/tests/e2e/live/waitForBridge.mjs` — parse the bridge-ready line from a log tail.
- `mcp-server/tests/e2e/live/mcpDriver.mjs` — spawn server + MCP client, `callTool` with retry.
- `mcp-server/tests/e2e/live/runner.mjs` — editor lifecycle + suite entry.
- `mcp-server/tests/e2e/live/flows/playmode.mjs` — Flow 1.
- `mcp-server/tests/e2e/live/flows/scripts.mjs` — Flow 2.
- `mcp-server/tests/e2e/live/selfcheck.mjs` — negative controls + reconnect stability.
- `mcp-server/tests/unit/e2e-live/verify.test.js`, `retry.test.js`, `waitForBridge.test.js` — CI-safe units.

**Modify:**
- `scripts/read-editor-log.mjs` — export its compile-parse function for reuse.
- `mcp-server/package.json` — add `test:e2e:live`.
- `.gitignore` — ignore the probe scratch file location if inside the repo (it is not — see Task 4).

---

### Task 1: Export the log parser + verify helpers

**Files:**
- Modify: `scripts/read-editor-log.mjs` (add named exports; keep the CLI behavior)
- Create: `mcp-server/tests/e2e/live/verify.mjs`
- Test: `mcp-server/tests/unit/e2e-live/verify.test.js`

**Interfaces:**
- Consumes: nothing.
- Produces: `verify.mjs` exports `fileExists(path) -> bool`, `readProbeEvents(path) -> Array<object>`,
  `parseCompile(logText) -> { compiled: bool, errors: Array<{file,line,code,msg}> }`.
  `scripts/read-editor-log.mjs` gets `export` on its existing `summarize(text) -> { errors, assemblies }` (which
  already de-dupes Unity's double-logged errors), and its CLI block is guarded to run only when invoked as main, so
  importing it has no side effects.

- [ ] **Step 1: Write the failing test** — `mcp-server/tests/unit/e2e-live/verify.test.js`

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, mkdtempSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileExists, readProbeEvents, parseCompile } from '../../e2e/live/verify.mjs';

const dir = mkdtempSync(join(tmpdir(), 'e2e-verify-'));

test('fileExists true/false', () => {
  const p = join(dir, 'a.txt'); writeFileSync(p, 'x');
  assert.equal(fileExists(p), true);
  assert.equal(fileExists(join(dir, 'nope.txt')), false);
});

test('readProbeEvents parses jsonl, tolerates blank/partial lines', () => {
  const p = join(dir, 'probe.jsonl');
  writeFileSync(p, '{"event":"probeLoaded"}\n{"event":"EnteredPlayMode"}\n\n');
  const ev = readProbeEvents(p);
  assert.equal(ev.length, 2);
  assert.equal(ev[1].event, 'EnteredPlayMode');
  assert.deepEqual(readProbeEvents(join(dir, 'missing.jsonl')), []); // missing file -> []
});

test('parseCompile detects a clean episode vs an error CS', () => {
  assert.deepEqual(parseCompile('Reloading assemblies\nAll good\n').errors, []);
  const bad = parseCompile('Reloading assemblies\nAssets/X.cs(3,5): error CS0103: broken\n');
  assert.equal(bad.errors.length, 1);
  assert.equal(bad.errors[0].code, 'CS0103');
});
```

- [ ] **Step 2: Run it, verify it fails**

Run: `cd mcp-server && node --test tests/unit/e2e-live/verify.test.js`
Expected: FAIL — `Cannot find module '../../e2e/live/verify.mjs'`.

- [ ] **Step 3: Export `summarize` + guard the CLI in `scripts/read-editor-log.mjs`**

`summarize` already does the episode-window scan + de-dupe; reuse it instead of duplicating. Two edits:

(a) Add `export` to its declaration: `function summarize(text) {` → `export function summarize(text) {`.

(b) The file currently runs its CLI (reads `Editor.log`, `process.exit`) at import. Wrap that whole block (from
`const path = editorLogPath();` to the final `process.exit(0);`) in a run-as-main guard so importing has no side
effects — add `import { pathToFileURL } from 'node:url';` to the top and:

```js
// Run the CLI only when invoked directly, not when imported (e.g. by the E2E harness verify helpers).
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const path = editorLogPath();
  if (!existsSync(path)) {
    console.error(`Editor.log not found at: ${path}\nSet UNITY_EDITOR_LOG to override.`);
    process.exit(0);
  }
  const { mtime } = statSync(path);
  const { errors, assemblies } = summarize(readFileSync(path, 'utf8'));
  console.log(`Editor.log: ${path}`);
  console.log(`last written: ${mtime.toISOString()}`);
  if (assemblies.length) {
    console.log('assemblies (last episode):');
    for (const a of assemblies) console.log(`  ${a.messages === 0 ? 'OK ' : '!! '}${a.assembly} (${a.messages} messages)`);
  }
  if (errors.length) {
    console.log(`\nFAIL — ${errors.length} compile error(s):`);
    for (const e of errors) console.log(`  ${e.file}(${e.line},${e.col}): ${e.code}: ${e.msg}`);
    process.exit(1);
  }
  console.log('\nPASS — no compile errors in the last episode.');
  process.exit(0);
}
```

- [ ] **Step 4: Implement `verify.mjs`**

```js
import { existsSync, readFileSync } from 'node:fs';
import { summarize } from '../../../../scripts/read-editor-log.mjs';

export function fileExists(path) {
  return existsSync(path);
}

export function readProbeEvents(path) {
  if (!existsSync(path)) return [];
  return readFileSync(path, 'utf8')
    .split(/\r?\n/)
    .map(l => l.trim())
    .filter(Boolean)
    .flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } });
}

export function parseCompile(logText) {
  const { errors } = summarize(logText);
  return { errors }; // no `compiled` flag — it can't tell "clean compile" from "no compile"; callers use errors.length
}
```

- [ ] **Step 5: Run tests, verify pass**

Run: `cd mcp-server && node --test tests/unit/e2e-live/verify.test.js`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add scripts/read-editor-log.mjs mcp-server/tests/e2e/live/verify.mjs mcp-server/tests/unit/e2e-live/verify.test.js
git commit -m "feat(e2e-live): verify helpers + reusable compile-log parser"
```

---

### Task 2: Retry-across-transient util

**Files:**
- Create: `mcp-server/tests/e2e/live/retry.mjs`
- Test: `mcp-server/tests/unit/e2e-live/retry.test.js`

**Interfaces:**
- Produces: `export async function retry(fn, { timeoutMs, intervalMs, onRetry }) -> <fn result>`. Calls `fn()`
  repeatedly; returns its first non-throwing result; throws the last error if `timeoutMs` elapses. Used by the driver
  to ride out the window where the editor is mid-domain-reload and the bridge is briefly gone.

- [ ] **Step 1: Write the failing test** — `mcp-server/tests/unit/e2e-live/retry.test.js`

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { retry } from '../../e2e/live/retry.mjs';

test('retry returns once fn stops throwing', async () => {
  let n = 0;
  const r = await retry(async () => { if (++n < 3) throw new Error('not yet'); return n; },
    { timeoutMs: 1000, intervalMs: 5 });
  assert.equal(r, 3);
});

test('retry throws the last error after timeout', async () => {
  await assert.rejects(
    retry(async () => { throw new Error('always'); }, { timeoutMs: 40, intervalMs: 5 }),
    /always/);
});
```

- [ ] **Step 2: Run it, verify it fails** — `node --test tests/unit/e2e-live/retry.test.js` → FAIL (module missing).

- [ ] **Step 3: Implement `retry.mjs`**

```js
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

export async function retry(fn, { timeoutMs = 60000, intervalMs = 500, onRetry } = {}) {
  const deadline = Date.now() + timeoutMs;
  let lastErr;
  // Loop until the deadline; Date.now() is fine here (runtime, not a workflow script).
  for (;;) {
    try { return await fn(); }
    catch (err) {
      lastErr = err;
      if (Date.now() >= deadline) throw lastErr;
      if (onRetry) onRetry(err);
      await sleep(intervalMs);
    }
  }
}
```

- [ ] **Step 4: Run tests, verify pass** — `node --test tests/unit/e2e-live/retry.test.js` → PASS (2).

- [ ] **Step 5: Commit**

```bash
git add mcp-server/tests/e2e/live/retry.mjs mcp-server/tests/unit/e2e-live/retry.test.js
git commit -m "feat(e2e-live): retry-across-transient util"
```

---

### Task 3: Wait-for-bridge log parser

**Files:**
- Create: `mcp-server/tests/e2e/live/waitForBridge.mjs`
- Test: `mcp-server/tests/unit/e2e-live/waitForBridge.test.js`

**Interfaces:**
- Produces: `export function bridgePort(logText) -> number|null` (parses `TcpTransport listening on 127.0.0.1:<port>`
  from the editor log tail; null if not present yet). `export async function waitForBridge(logPath, opts) -> number`
  (polls the file until the line appears; uses `retry`). The runner uses `bridgePort` under `retry`.

- [ ] **Step 1: Write the failing test** — `waitForBridge.test.js`

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bridgePort } from '../../e2e/live/waitForBridge.mjs';

test('bridgePort extracts the port', () => {
  assert.equal(bridgePort('...\n[UnityEditorMCP] TcpTransport listening on 127.0.0.1:6423\n...'), 6423);
});
test('bridgePort returns null before the line appears', () => {
  assert.equal(bridgePort('booting...\n'), null);
});
```

- [ ] **Step 2: Run it, verify it fails** → FAIL (module missing).

- [ ] **Step 3: Implement `waitForBridge.mjs`**

```js
import { readFileSync, existsSync } from 'node:fs';
import { retry } from './retry.mjs';

const RE = /TcpTransport listening on 127\.0\.0\.1:(\d+)(?=\r?\n)/; // EOL-anchored: no partial-flush port capture

export function bridgePort(logText) {
  const m = logText.match(RE);
  return m ? Number(m[1]) : null;
}

export async function waitForBridge(logPath, { timeoutMs = 180000, intervalMs = 1000 } = {}) {
  return retry(() => {
    const port = existsSync(logPath) ? bridgePort(readFileSync(logPath, 'utf8')) : null;
    if (port == null) throw new Error('bridge not up yet');
    return port;
  }, { timeoutMs, intervalMs });
}
```

- [ ] **Step 4: Run tests, verify pass** → PASS (2).

- [ ] **Step 5: Commit**

```bash
git add mcp-server/tests/e2e/live/waitForBridge.mjs mcp-server/tests/unit/e2e-live/waitForBridge.test.js
git commit -m "feat(e2e-live): wait-for-bridge log parser"
```

---

### Task 4: The `e2e-host` project + the in-editor probe

**Files:**
- Create: `ci/e2e-host/Packages/manifest.json`
- Create: `ci/e2e-host/Assets/Editor/E2EProbe.cs`
- Create: `ci/e2e-host/Assets/Editor/E2EProbe.cs.meta` (generated by Unity in Step 3; commit it)
- Modify: `.gitignore` (ensure only `Library/` etc. are ignored — the probe writes OUTSIDE the repo, so nothing to add)

**Interfaces:**
- Produces: a warm, committed host project the runner launches; a probe that appends play-mode events (JSON-per-line)
  to the file named by the `E2E_PROBE_FILE` env var. Event strings are the stable `PlayModeStateChange` enum names
  (`EnteredPlayMode`/`ExitingPlayMode`/`EnteredEditMode`/`ExitingEditMode`) plus `{"event":"pause","paused":bool}` and
  a startup `{"event":"probeLoaded"}`.

- [ ] **Step 1: Write `ci/e2e-host/Packages/manifest.json`** (mirrors the floor host, drops `testables` — the harness
  drives via the bridge, it does not run NUnit here)

```json
{
  "dependencies": {
    "com.burakk.unity-editor-mcp": "file:../../../unity-editor-mcp",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0",
    "com.unity.modules.physics": "1.0.0",
    "com.unity.modules.ui": "1.0.0",
    "com.unity.modules.uielements": "1.0.0"
  }
}
```

- [ ] **Step 2: Write the probe `ci/e2e-host/Assets/Editor/E2EProbe.cs`** (C# 7.3, netstandard 2.0, IMGUI-safe;
  no-op unless `E2E_PROBE_FILE` is set, so a human opening the project is unaffected)

```csharp
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// E2E harness probe: mirrors play-mode lifecycle to a JSONL file the Node harness reads.
// Re-subscribes on every domain reload via [InitializeOnLoad], so events that straddle a reload are still captured.
[InitializeOnLoad]
public static class E2EProbe
{
    static readonly string ProbeFile = Environment.GetEnvironmentVariable("E2E_PROBE_FILE");

    static E2EProbe()
    {
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.pauseStateChanged += OnPause;
        Append("{\"event\":\"probeLoaded\"}");
    }

    static void OnPlayMode(PlayModeStateChange s)
    {
        Append("{\"event\":\"" + s + "\"}");
    }

    static void OnPause(PauseState s)
    {
        Append("{\"event\":\"pause\",\"paused\":" + (s == PauseState.Paused ? "true" : "false") + "}");
    }

    static void Append(string line)
    {
        if (string.IsNullOrEmpty(ProbeFile)) return;
        try { File.AppendAllText(ProbeFile, line + "\n"); }
        catch { /* never let the probe break the editor */ }
    }
}
```

- [ ] **Step 3: Provision the warm host** (one-time; generates `ProjectSettings` + `Library` + the `.cs.meta`)

Run (batch is fine here — no play-mode; just import + compile):
`"$UNITY_PATH" -batchmode -projectPath ci/e2e-host -quit -logFile /tmp/e2e-provision.log`
Expected: exit 0; `grep -c "error CS" /tmp/e2e-provision.log` → `0` (the probe compiled); `ci/e2e-host/Library/` and
`ci/e2e-host/ProjectSettings/` now exist; `ci/e2e-host/Assets/Editor/E2EProbe.cs.meta` was generated.

- [ ] **Step 4: Verify `Library` is git-ignored, `ProjectSettings` + probe are trackable**

Run: `git status --porcelain ci/e2e-host | grep -E "Library/" || echo "Library ignored OK"`
Expected: `Library ignored OK` (the global `[Ll]ibrary/` rule covers it). `ProjectSettings/`, `Packages/manifest.json`,
`Assets/Editor/E2EProbe.cs`, and `E2EProbe.cs.meta` should appear as untracked.

- [ ] **Step 5: Commit the skeleton (never the `Library`)**

```bash
git add ci/e2e-host/Packages/manifest.json ci/e2e-host/ProjectSettings ci/e2e-host/Assets
git commit -m "feat(e2e-live): e2e-host project skeleton + in-editor play-mode probe"
```

---

### Task 5: MCP driver + runner (live smoke)

**Files:**
- Create: `mcp-server/tests/e2e/live/mcpDriver.mjs`
- Create: `mcp-server/tests/e2e/live/runner.mjs`
- Modify: `mcp-server/package.json` (add `test:e2e:live`)

**Interfaces:**
- Consumes: `waitForBridge`, `retry`.
- Produces: `mcpDriver.mjs` exports `class McpDriver { async start(hostPath); async call(toolName, args); async
  stop(); }` — `call` invokes the editor tool via `call_unity_tool`, wrapped in `retry` so a mid-reload failure is
  ridden out. `runner.mjs` exports `async function launchEditor({unityPath, hostPath, logPath, probeFile}) ->
  { proc, port }`, `async function shutdown(state)`, and a CLI entry that runs the selected flows and sets the process
  exit code.

- [ ] **Step 1: Implement `mcpDriver.mjs`** (drives the real chain; `call_unity_tool` per CLAUDE.md is the generic
  invoke path after discovery)

```js
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { retry } from './retry.mjs';

const SERVER = resolve(dirname(fileURLToPath(import.meta.url)), '../../../src/core/server.js');

export class McpDriver {
  async start(hostPath) {
    this.transport = new StdioClientTransport({
      command: 'node', args: [SERVER],
      env: { ...process.env, UNITY_PROJECT_PATH: hostPath }, // server derives the same per-project port as the editor
    });
    this.client = new Client({ name: 'e2e-live', version: '1.0.0' }, { capabilities: {} });
    await this.client.connect(this.transport);
  }

  // Verify-by-outcome friendly: retries across the editor's domain-reload window.
  async call(toolName, args = {}, { timeoutMs = 90000 } = {}) {
    return retry(async () => {
      const res = await this.client.callTool({
        name: 'call_unity_tool', arguments: { toolName, parameters: args },
      });
      if (res.isError) throw new Error(`tool ${toolName} error: ${JSON.stringify(res.content)}`);
      return res;
    }, { timeoutMs, intervalMs: 750 });
  }

  async stop() { try { await this.client?.close(); } catch {} }
}
```

- [ ] **Step 2: Implement `runner.mjs`** (editor lifecycle + CLI)

```js
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { waitForBridge } from './waitForBridge.mjs';
import { McpDriver } from './mcpDriver.mjs';

export async function launchEditor({ unityPath, hostPath, logPath, probeFile }) {
  writeFileSync(probeFile, ''); // truncate probe scratch
  const proc = spawn(unityPath, ['-projectPath', hostPath, '-logFile', logPath], { // headed: NO -batchmode
    env: { ...process.env, E2E_PROBE_FILE: probeFile },
    stdio: 'ignore', detached: false,
  });
  const port = await waitForBridge(logPath, { timeoutMs: Number(process.env.E2E_TIMEOUT ?? 180000) });
  return { proc, port };
}

export async function shutdown(state) {
  await state.driver?.stop();
  try { state.editor?.proc.kill(); } catch {}
}

async function main() {
  const unityPath = process.env.UNITY_PATH;
  const hostPath = process.env.E2E_HOST_PROJECT || 'ci/e2e-host';
  if (!unityPath) { console.error('UNITY_PATH is required'); process.exit(2); }

  const scratch = mkdtempSync(join(tmpdir(), 'e2e-live-'));
  const probeFile = join(scratch, 'probe.jsonl');
  const logPath = join(scratch, 'editor.log');
  const which = process.argv.find(a => a.startsWith('--flow='))?.split('=')[1] || 'all';

  const state = {};
  let failed = 0;
  try {
    state.editor = await launchEditor({ unityPath, hostPath, logPath, probeFile });
    state.driver = new McpDriver();
    await state.driver.start(hostPath);

    const ctx = { driver: state.driver, hostPath, probeFile, logPath };
    const flows = [];
    if (which === 'all' || which === 'playmode') flows.push((await import('./flows/playmode.mjs')).run);
    if (which === 'all' || which === 'scripts') flows.push((await import('./flows/scripts.mjs')).run);

    for (const run of flows) {
      try { await run(ctx); console.log(`PASS ${run.flowName}`); }
      catch (e) { failed++; console.error(`FAIL ${run.flowName}: ${e.message}`); }
    }
  } finally {
    await shutdown(state);
    rmSync(scratch, { recursive: true, force: true });
  }
  process.exit(failed ? 1 : 0);
}

// Run as CLI when invoked directly.
if (process.argv[1] && process.argv[1].endsWith('runner.mjs')) main();
```

- [ ] **Step 3: Add the npm script** — in `mcp-server/package.json` `scripts`:

```json
"test:e2e:live": "node tests/e2e/live/runner.mjs"
```

- [ ] **Step 4: Live smoke** — with a stub flow, confirm launch→connect→quit works. Temporarily create
  `mcp-server/tests/e2e/live/flows/playmode.mjs` with:

```js
export async function run(ctx) { const r = await ctx.driver.call('get_editor_state'); if (r.isError) throw new Error('no state'); }
run.flowName = 'playmode';
```

Run: `cd mcp-server && UNITY_PATH="<your editor>" node tests/e2e/live/runner.mjs --flow=playmode`
Expected: a headed editor opens on `ci/e2e-host`, the log shows `TcpTransport listening on 127.0.0.1:<port>`, console
prints `PASS playmode`, exit 0, editor is killed on teardown.

- [ ] **Step 5: Commit**

```bash
git add mcp-server/tests/e2e/live/mcpDriver.mjs mcp-server/tests/e2e/live/runner.mjs mcp-server/tests/e2e/live/flows/playmode.mjs mcp-server/package.json
git commit -m "feat(e2e-live): MCP driver + editor-lifecycle runner + test:e2e:live"
```

---

### Task 6: Flow 1 — play-mode (live)

**Files:**
- Modify: `mcp-server/tests/e2e/live/flows/playmode.mjs` (replace the Task-5 stub with the real flow)

**Interfaces:**
- Consumes: `ctx.driver.call`, `verify.readProbeEvents`, `ctx.probeFile`. Tools: `get_editor_state`, `play_game`,
  `pause_game`, `stop_game`.
- Produces: `export async function run(ctx)` with `run.flowName = 'playmode'`.

- [ ] **Step 1: Implement the flow** (verify-by-outcome; both channels; `finally` cleanup)

```js
import assert from 'node:assert/strict';
import { readProbeEvents } from '../verify.mjs';

const hasEvent = (probeFile, name) => readProbeEvents(probeFile).some(e => e.event === name);
const isPlaying = async (driver) => {
  const r = await driver.call('get_editor_state');
  return JSON.parse(r.content[0].text).isPlaying === true; // read-back (bridge contract)
};

export async function run(ctx) {
  const { driver, probeFile } = ctx;
  try {
    assert.equal(await isPlaying(driver), false, 'precondition: not playing');

    await driver.call('play_game');                        // crosses a domain reload
    assert.equal(await isPlaying(driver), true, 'read-back: entered play mode');
    assert.ok(hasEvent(probeFile, 'EnteredPlayMode'), 'probe: entered play mode'); // independent

    await driver.call('pause_game');
    assert.ok(readProbeEvents(probeFile).some(e => e.event === 'pause' && e.paused === true), 'probe: paused');

    await driver.call('stop_game');                        // crosses a domain reload
    assert.equal(await isPlaying(driver), false, 'read-back: exited play mode');
    assert.ok(hasEvent(probeFile, 'ExitingPlayMode'), 'probe: exiting play mode');
  } finally {
    if (await isPlaying(driver).catch(() => false)) { try { await driver.call('stop_game'); } catch {} }
  }
}
run.flowName = 'playmode';
```

- [ ] **Step 2: Run the flow live**

Run: `cd mcp-server && UNITY_PATH="<editor>" node tests/e2e/live/runner.mjs --flow=playmode`
Expected: `PASS playmode`, exit 0. (If `get_editor_state` returns no `isPlaying`, note it — see design §10; fall back to
the probe-only assertions and record the gap.)

- [ ] **Step 3: Verify it can fail** — temporarily change the precondition assert to `true`, rerun, confirm
  `FAIL playmode`, then revert. (Proves the flow actually asserts.)

- [ ] **Step 4: Commit**

```bash
git add mcp-server/tests/e2e/live/flows/playmode.mjs
git commit -m "feat(e2e-live): play-mode flow (probe + read-back, reload-surviving)"
```

---

### Task 7: Flow 2 — script CRUD + recompile (live)

**Files:**
- Create: `mcp-server/tests/e2e/live/flows/scripts.mjs`

**Interfaces:**
- Consumes: `ctx.driver.call`, `verify.fileExists`, `verify.parseCompile`, `ctx.hostPath`, `ctx.logPath`. Tools:
  `create_script`, `read_script`, `update_script`, `delete_script`, `list_scripts`, `refresh_assets`,
  `get_compilation_state`, `add_component`, `create_gameobject`, `delete_gameobject`.
- Produces: `export async function run(ctx)` with `run.flowName = 'scripts'`.

- [ ] **Step 1: Implement the flow** (mixed verification; failure path; `finally` cleanup)

```js
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileExists, parseCompile } from '../verify.mjs';

const CLASS = 'E2EScratch';
const REL = `Assets/${CLASS}.cs`;
const VALID = `using UnityEngine;\npublic class ${CLASS} : MonoBehaviour {}\n`;
const BROKEN = `using UnityEngine;\npublic class ${CLASS} : MonoBehaviour { void X() { return 1; } }\n`;
const logHasError = (logPath) => parseCompile(readFileSync(logPath, 'utf8')).errors.length > 0;

export async function run(ctx) {
  const { driver, hostPath, logPath } = ctx;
  const abs = join(hostPath, REL);
  try {
    // create -> filesystem + read-back
    await driver.call('create_script', { path: REL, contents: VALID });
    assert.ok(fileExists(abs), 'filesystem: .cs exists');
    await driver.call('read_script', { path: REL }); // read-back: returns without error

    // recompile -> independent compile signal + read-back the new type is live
    await driver.call('refresh_assets');             // crosses a domain reload
    assert.ok(!logHasError(logPath), 'compile log: clean recompile');
    const go = JSON.parse((await driver.call('create_gameobject', { name: 'E2EProbeTarget' })).content[0].text);
    await driver.call('add_component', { gameObjectPath: '/E2EProbeTarget', componentType: CLASS }); // proves it compiled AND loaded

    // failure path: a broken script must surface error CS (proves the recompile ran + the tool wrote the bad code)
    await driver.call('update_script', { path: REL, contents: BROKEN });
    await driver.call('refresh_assets');
    assert.ok(logHasError(logPath), 'compile log: error CS on broken script (failure path)');
    await driver.call('update_script', { path: REL, contents: VALID });
    await driver.call('refresh_assets');
    assert.ok(!logHasError(logPath), 'compile log: clean after fix');

    // delete -> filesystem gone + read-back gone
    await driver.call('delete_gameobject', { path: '/E2EProbeTarget' });
    await driver.call('delete_script', { path: REL });
    assert.ok(!fileExists(abs), 'filesystem: .cs deleted');
    const list = JSON.parse((await driver.call('list_scripts')).content[0].text);
    assert.ok(!JSON.stringify(list).includes(REL), 'read-back: no longer listed');
  } finally {
    // leave the shared editor clean for the next flow, regardless of failure
    try { await driver.call('delete_gameobject', { path: '/E2EProbeTarget' }); } catch {}
    if (fileExists(abs)) { try { await driver.call('delete_script', { path: REL }); await driver.call('refresh_assets'); } catch {} }
  }
}
run.flowName = 'scripts';
```

- [ ] **Step 2: Run the flow live**

Run: `cd mcp-server && UNITY_PATH="<editor>" node tests/e2e/live/runner.mjs --flow=scripts`
Expected: `PASS scripts`, exit 0. Confirm at run time that the exact param names (`path`/`contents`, `gameObjectPath`,
`componentType`) match the catalog; adjust to the catalog's real names if they differ (see design §10).

- [ ] **Step 3: Verify it can fail** — temporarily invert the `!logHasError` assertion after the clean recompile,
  rerun, confirm `FAIL scripts`, revert.

- [ ] **Step 4: Commit**

```bash
git add mcp-server/tests/e2e/live/flows/scripts.mjs
git commit -m "feat(e2e-live): script CRUD + recompile flow (mixed verification, failure path)"
```

---

### Task 8: Harness self-validation + docs

**Files:**
- Create: `mcp-server/tests/e2e/live/selfcheck.mjs`
- Create: `mcp-server/tests/unit/e2e-live/selfcheck.test.js` (the CI-safe negative controls)
- Modify: `mcp-server/tests/e2e/live/runner.mjs` (wire `--selfcheck`)
- Modify: `docs/setup-guide.md` (a short "Live E2E harness" section: prerequisites, provisioning, running)

**Interfaces:**
- Consumes: `verify.mjs`. Produces: `export function assertProbeLive(probeFile)` (throws if the probe wrote no
  `probeLoaded`), and a CI-safe negative-control test proving the verify helpers actually fail on a false premise.

- [ ] **Step 1: Write the failing CI-safe negative-control test** — `selfcheck.test.js`

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fileExists, readProbeEvents } from '../../e2e/live/verify.mjs';
import { assertProbeLive } from '../../e2e/live/selfcheck.mjs';

test('negative control: file that was never written is absent', () => {
  assert.equal(fileExists('/no/such/e2e/path.cs'), false); // a false "it exists" premise must be caught
});
test('assertProbeLive throws when the probe never loaded', () => {
  assert.throws(() => assertProbeLive('/no/such/probe.jsonl'), /probe/i);
});
```

- [ ] **Step 2: Run it, verify it fails** → FAIL (`selfcheck.mjs` missing).

- [ ] **Step 3: Implement `selfcheck.mjs`**

```js
import { readProbeEvents } from './verify.mjs';

export function assertProbeLive(probeFile) {
  const loaded = readProbeEvents(probeFile).some(e => e.event === 'probeLoaded');
  if (!loaded) throw new Error('probe liveness: no probeLoaded event — a silent probe failure would read as pass');
}
```

- [ ] **Step 4: Run tests, verify pass** → PASS (2).

- [ ] **Step 5: Wire probe-liveness + reconnect-stability into the runner** — after `driver.start`, before the flows:

```js
import { assertProbeLive } from './selfcheck.mjs';
// ...after a first successful driver.call to ensure the editor pumped once:
await ctx.driver.call('get_editor_state');
assertProbeLive(probeFile); // fail fast if the probe is silent
```

For **reconnect stability**, when `--selfcheck` is passed, push `playmode.run` into the flow list **twice** so
`runner.main`'s existing loop runs it a second time (a fresh reload cycle through one shared editor); both must report
`PASS`. No new code — just a second `flows.push((await import('./flows/playmode.mjs')).run)` under the `--selfcheck`
guard.

- [ ] **Step 6: Document** — add to `docs/setup-guide.md` a "Live E2E harness (local)" section: requires a local
  editor; `UNITY_PATH=<editor> npm run -w mcp-server test:e2e:live`; one-time provisioning of `ci/e2e-host`
  (Task 4 Step 3); env `E2E_HOST_PROJECT`, `E2E_TIMEOUT`; explicitly not part of `npm test` or the floor-matrix.

- [ ] **Step 7: Run the full CI-safe unit suite + a full live run**

Run: `cd mcp-server && node --test tests/unit/e2e-live/*.test.js` → PASS (all).
Run: `UNITY_PATH="<editor>" node tests/e2e/live/runner.mjs --selfcheck` → both flows PASS twice, exit 0.

- [ ] **Step 8: Commit**

```bash
git add mcp-server/tests/e2e/live/selfcheck.mjs mcp-server/tests/unit/e2e-live/selfcheck.test.js mcp-server/tests/e2e/live/runner.mjs docs/setup-guide.md
git commit -m "feat(e2e-live): harness self-validation (negative controls, probe liveness, reconnect stability) + docs"
```

---

## Notes for the implementer

- **Exact tool/param names:** confirm `play_game`/`pause_game`/`stop_game`/`get_editor_state` and the script tools'
  param names against `protocol/catalog/commands.json` at the top of Tasks 6–7; the plan uses the most likely names.
  `get_editor_state`'s `isPlaying`/`isPaused` field presence is an open item (design §10) — fall back to probe-only for
  pause if absent, and record it.
- **`call_unity_tool` shape:** verify the argument shape (`{ toolName, parameters }`) against
  `mcp-server/src/handlers/.../CallUnityToolToolHandler.js` in Task 5; adjust `McpDriver.call` if it differs.
- **Reload response race:** `play_game`/`refresh_assets` may not return a clean response (the reload swallows it); the
  `retry` wrapper + verify-by-outcome handle this — never assert on the transition call's own return.
- **One editor per run:** all flows share the editor started in `runner.main`; per-flow `finally` cleanup is what keeps
  them isolated. Do not add per-flow relaunches.
- **Crash-recovery relaunch is deferred (v1 scope call):** v1 fails a flow fast when the bridge stays dead past
  `E2E_TIMEOUT` (the `retry` throws — spec §6's reconnect-timeout path). Auto-relaunching a crashed editor once (the
  other half of spec §6) is a v1.1 refinement: the core play-mode/script scope does not trigger the `giconv` crash
  (that is a `run_tests`-on-long-lived-editor risk, deferred), so fail-fast is acceptable now. Revisit when the
  `run_tests` flow is added.
