# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Direction (read first)

This repository is the base of a fork whose mission is defined in
`.claude/unity-mcp-fork-requirements.md` — **the deep, floor-true MCP bridge for older Unity
projects**. That document is the authoritative roadmap (requirements A1–K6, prioritized P0/P1/P2).
Consult it before designing or implementing any feature.

Compatibility target: **Unity 2019 through the latest versions**, with the initial focus on the
**2020.3 – 2022.3 LTS range**. Supporting very old editors (down to 2019) is the purpose of this
fork — never silently raise the version floor:

- Every version-divergent Unity API goes behind `#if UNITY_X_Y_OR_NEWER` guards with **both
  branches maintained** — guards, not floors.
- Unity-side C# must stay within **C# 8 / netstandard 2.0** (the 2020.3 Mono reality); code that
  must compile on 2019.4 is limited to **C# 7.3**. No UI Toolkit editor APIs in core paths
  (IMGUI-safe).
- Node server stays **pure JS, no native modules** (npx friction-free), Node >= 18.
- All guarded API sites are cataloged in **`COMPATIBILITY.md`** (keep it in sync when adding a
  guard). Current guards: Rigidbody drag/damping renames (`UNITY_6000_0_OR_NEWER`) and the
  `PrefabStageUtility` namespace move (`UNITY_2021_2_OR_NEWER`, `AssetManagementHandler.cs`).
- The Unity package `unity` field (`unity-editor-mcp/package.json`, currently `2020.3`), the README
  support claims, and actual code compatibility must agree. There is **no CI matrix compiling the C#
  on the floor yet** — the largest open compatibility risk (see `COMPATIBILITY.md`).

## Repository Layout

Three major parts (see `docs/adr/0001-protocol-contract-and-three-part-architecture.md`):

- `protocol/` — the **communication contract**: the canonical command catalog
  (`catalog/commands.json`), the wire/result/error spec, the protocol version line (`VERSION`), and
  the drift gate. A versioned sub-project both halves must conform to. See `protocol/README.md`.
- `unity-editor-mcp/` — Unity UPM package (`com.burakk.unity-editor-mcp`), the C# editor side, split into
  two assemblies along the Unity-dependency seam (ADR 0002): `Core/` (`UnityEditorMCP.Core`,
  `noEngineReferences` — framing, dispatch, the result/error contract; zero Unity refs, so it's
  `dotnet`-testable) and `Editor/` (`UnityEditorMCP.Editor` — the `[InitializeOnLoad]` bootstrap,
  handlers, and all `#if UNITY_*` guards). Depends on `com.unity.nuget.newtonsoft-json`. Installed
  via git URL with `?path=unity-editor-mcp`.
- `mcp-server/` — Node.js MCP **adapter + transport client** (npm package `@burakaydinofficial/unity-editor-mcp`). Pure
  ES modules (`"type": "module"`), only runtime dependency is `@modelcontextprotocol/sdk`.
- `dotnet/UnityEditorMCP.Core.Tests/` — xUnit project that compiles the Unity-independent `Core`
  source and runs it via `dotnet test` (no Unity, no license).
- `docs/` — ADRs (`docs/adr/`) and historical phase planning/progression documents.

## Commands

All Node.js work happens in `mcp-server/`:

```bash
cd mcp-server
npm install

npm test                    # unit + integration tests (Node built-in test runner, no Jest/Mocha)
npm run test:unit           # unit tests only
npm run test:integration    # integration tests only
npm run test:e2e            # requires a running Unity Editor with the package installed
npm run test:coverage       # c8 coverage (lcov + text + html)
npm run test:ci             # what CI runs (.github/workflows/test-coverage.yml)

# Run a single test file:
node --test tests/unit/handlers/instances/MetaTools.test.js

npm start                   # run the MCP server (node src/core/server.js)
npm run dev                 # run with --watch
```

Test environment notes: `NODE_ENV=test` or `CI=true` makes `UnityConnection.connect()` refuse to
connect (so unit/integration tests never need a live editor); `DISABLE_AUTO_RECONNECT=true`
disables the reconnect loop. Server config env vars: `UNITY_HOST`, `UNITY_PORT` (explicit port,
wins over discovery), `UNITY_PROJECT_PATH` (resolve the target editor via the discovery registry
or the derived per-project port — ADR 0003), `UNITY_MCP_REGISTRY_DIR` (registry dir override),
`LOG_LEVEL` (`info`/`debug`). The advertised MCP surface is always the three generic meta-tools
(ADR 0004/0006); editor commands are reached via `call_unity_tool` after on-demand discovery. Editor-side
env: `UNITY_MCP_PORT` overrides the derived port.

Unity-side tests are NUnit EditMode tests (`unity-editor-mcp/Tests/Editor/`) and run through the
Unity Test Runner inside an editor — they cannot be run from the command line in this repo alone.
Local host projects for the floor matrix live in the git-ignored `unity-test-projects/`
(2020.3 / 2021.3 / 2022.3), wired to the package via `file:` refs and `testables`.

The protocol contract has its own dependency-free tooling (run from `protocol/`):

```bash
node scripts/check-drift.mjs       # fail if server/editor diverge from catalog/commands.json
node scripts/bootstrap-catalog.mjs # re-seed the catalog from current code (re-baseline only)
```

The Unity-independent `Core` is tested without Unity (needs the .NET SDK):

```bash
cd dotnet/UnityEditorMCP.Core.Tests
dotnet test            # framing, dispatch, result/error contract — pinned to C# 8, no editor
```

Compatibility lint (pure Node, from repo root) — flags floor-divergent Unity APIs used outside an
`#if` guard (would have caught the PrefabStage floor break):

```bash
node scripts/compat-lint.mjs
```

## Architecture

Communication chain:

```
MCP client (Claude/Cursor) ⇄ stdio (JSON-RPC) ⇄ Node server ⇄ TCP loopback (discovered/derived per-project port; ADR 0003) ⇄ Unity Editor
```

The TCP protocol is length-prefixed JSON: a 4-byte big-endian length header followed by a UTF-8
JSON payload, in both directions. Framing/parsing lives in
`mcp-server/src/core/unityConnection.js` (Node side, includes 1MB message cap and corrupt-frame
recovery) and the Unity-independent `unity-editor-mcp/Core/` (`TcpTransport` accept/read loop +
`MessageFramer` in `Framing.cs`, same 1MB cap), wired into the editor by
`Editor/Core/UnityEditorMCP.cs`. Commands carry an `id`; the Node side correlates responses via a
pending-command map with a 30s timeout and reconnects with exponential backoff.

**Node side** (`mcp-server/src/`):
- `core/server.js` — MCP protocol wiring; converts handler results to MCP content responses.
- `core/unityConnection.js` — TCP client, framing, command correlation, auto-reconnect.
- `core/config.js` — config + logger. **All logging must go to stderr** (`console.error`); stdout
  is reserved for MCP JSON-RPC and any stray stdout output breaks the protocol.
- `handlers/<category>/<Name>ToolHandler.js` — one class per MCP tool, extends
  `handlers/base/BaseToolHandler.js` (holds `name`, `description`, `inputSchema`; `validate()`
  checks required fields; `execute()` calls `this.unityConnection.sendCommand(type, params)`).
  Handlers return `{status: 'success', result}` or `{status: 'error', error, code, details}`.
- `handlers/index.js` — the `HANDLER_CLASSES` registry (just the 3 generic meta-tools — ADR 0006);
  `createHandlers(manager)` instantiates each with the `UnityConnectionManager` (it resolves a
  connection per call by explicit instance). The meta-tools call `this.manager.requireConnection(...)`;
  the few Node-logic handlers (`core/nodeLogicTools.js`) take a resolved connection.

**Unity side** (`unity-editor-mcp/Editor/`):
- `Core/UnityEditorMCP.cs` — `[InitializeOnLoad]` static class. Starts Core's `TcpTransport` on a
  **derived per-project port** (`ResolveInitialPort` → `EndpointAddressing.DerivePort`, ADR 0003;
  `UNITY_MCP_PORT` overrides), re-arms after every domain reload (and unbinds the listener on
  `beforeAssemblyReload`). Incoming commands are queued and executed on the **main thread** via
  `EditorApplication.update` (Unity APIs are not thread-safe). Every command is dispatched through
  Core's `CommandDispatcher` (`DispatchViaCore`); the legacy `ProcessCommand` switch was fully retired
  in v0.4.0 (an unregistered type yields `UNKNOWN_COMMAND`).
- `Handlers/*Handler.cs` — static classes doing the actual editor work, taking `JObject`
  parameters. Some are one-method-per-command (`GameObjectHandler.CreateGameObject`), others use an
  `action` parameter dispatched through `HandleCommand(action, params)` (tags, layers, selection,
  windows, asset database…).
- `Helpers/Response.cs` — builds the JSON responses (`Response.SuccessResult(id, data)` /
  `Response.ErrorResult(id, message, code, details)`). The Node side accepts both the old
  (`status`) and new (`success`/`id`) shapes. Domain errors are no longer laundered as success:
  `Response.Result` delegates classification to `Core.ResponseClassifier.Classify` (Unity-independent,
  dotnet-tested), which turns a handler's `{ error: … }` return into a real `ErrorResult` rather than
  a success envelope.

**Unity-side layering (ADR 0002).** A Unity-independent `Core` assembly (`unity-editor-mcp/Core/`)
holds framing, the command/result models, the dispatcher, and the wire-truth classifier —
`HandlerOutcome`/`CommandResult` form a discriminated result that *cannot* serialize an error as a
success, all covered by `dotnet test`. The live transport runs on Core's `TcpTransport`, and Core's
`CommandDispatcher` is the **sole** dispatch front (v0.4.0): every command is registered on it
(`BuildDispatcher` in `Editor/Core/UnityEditorMCP.cs`) and served by the tested rail; an unregistered
type yields `UNKNOWN_COMMAND`. The legacy `ProcessCommand` switch has been fully retired. The
dispatcher also applies the reserved `fields` meta-param (GraphQL-style result projection) uniformly
to every command's success payload.

### Adding a new editor command

The command surface is governed by the protocol catalog. As of v0.5.0 (ADR 0006) the Node server is a
3-tool generic surface — an editor command is reached via `call_unity_tool` after on-demand discovery,
so it needs **no Node handler** and no `HANDLER_CLASSES` entry:

1. Add the command to `protocol/catalog/commands.json` (`name`, `sides: ["editor"]`, `params` schema,
   optional `result` schema, category).
2. C# handler method in `unity-editor-mcp/Editor/Handlers/` returning `HandlerOutcome` (`Ok(payload)`
   / `Fail(message, code)`), registered on the rail in `BuildDispatcher` (`Editor/Core/UnityEditorMCP.cs`):
   `dispatcher.Register("tool_name", Handler.Method)` (single-method) or a lambda for an
   action-dispatch handler.
3. Regenerate the editor manifest so the editor advertises the new command's params + result:
   `node protocol/scripts/generate-csharp-catalog.mjs`; then `node protocol/scripts/check-drift.mjs`
   must pass. Add an editor (NUnit) test. The Node side needs nothing — the agent discovers and invokes
   the command via `list_unity_tools` / `call_unity_tool`.

**Node-logic exception (rare).** Only when a command needs genuine Node-side logic (a security boundary,
client-side generation) add a handler under `mcp-server/src/handlers/<category>/` and register it in
`core/nodeLogicTools.js` (`NODE_LOGIC_TOOLS`) — `call_unity_tool` then dispatches to that handler
instead of forwarding raw. It is NOT added to `HANDLER_CLASSES` (that is the 3 meta-tools only).

Tool names are `verb_noun` snake_case and must match across the catalog, the C# dispatcher
registration, and the `sendCommand` type string. Keep new Unity API usage guarded per `COMPATIBILITY.md`.
