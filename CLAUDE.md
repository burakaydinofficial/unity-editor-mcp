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
- `unity-editor-mcp/` — Unity UPM package (`com.unity.editor-mcp`), the C# editor **domain engine**
  under `Editor/`. Depends on `com.unity.nuget.newtonsoft-json`. Installed into a Unity project via
  git URL with `?path=unity-editor-mcp`.
- `mcp-server/` — Node.js MCP **adapter + transport client** (npm package `unity-editor-mcp`). Pure
  ES modules (`"type": "module"`), only runtime dependency is `@modelcontextprotocol/sdk`.
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
node --test tests/unit/handlers/PingToolHandler.test.js

npm start                   # run the MCP server (node src/core/server.js)
npm run dev                 # run with --watch
```

Test environment notes: `NODE_ENV=test` or `CI=true` makes `UnityConnection.connect()` refuse to
connect (so unit/integration tests never need a live editor); `DISABLE_AUTO_RECONNECT=true`
disables the reconnect loop. Server config env vars: `UNITY_HOST`, `UNITY_PORT` (default 6400),
`LOG_LEVEL` (`info`/`debug`).

Unity-side tests are NUnit EditMode tests (`unity-editor-mcp/Tests/Editor/`, plus
`Editor/Tests/`) and run through the Unity Test Runner inside an editor — they cannot be run from
the command line in this repo alone.

The protocol contract has its own dependency-free tooling (run from `protocol/`):

```bash
node scripts/check-drift.mjs       # fail if server/editor diverge from catalog/commands.json
node scripts/bootstrap-catalog.mjs # re-seed the catalog from current code (re-baseline only)
```

## Architecture

Communication chain:

```
MCP client (Claude/Cursor) ⇄ stdio (JSON-RPC) ⇄ Node server ⇄ TCP localhost:6400 ⇄ Unity Editor
```

The TCP protocol is length-prefixed JSON: a 4-byte big-endian length header followed by a UTF-8
JSON payload, in both directions. Framing/parsing lives in
`mcp-server/src/core/unityConnection.js` (Node side, includes 1MB message cap and corrupt-frame
recovery) and `unity-editor-mcp/Editor/Core/UnityEditorMCP.cs` (`SendFramedMessage` /
`HandleClientAsync`). Commands carry an `id`; the Node side correlates responses via a
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
- `handlers/index.js` — the `HANDLER_CLASSES` registry; `createHandlers()` instantiates every
  handler with the shared `UnityConnection`.

**Unity side** (`unity-editor-mcp/Editor/`):
- `Core/UnityEditorMCP.cs` — `[InitializeOnLoad]` static class. Starts a `TcpListener` on
  loopback:6400, re-arms automatically after every domain reload. Incoming commands are queued and
  executed on the **main thread** via `EditorApplication.update` (Unity APIs are not thread-safe).
  Dispatch is a big `switch (command.Type)` in `ProcessCommand` mapping command type strings to
  static handler classes.
- `Handlers/*Handler.cs` — static classes doing the actual editor work, taking `JObject`
  parameters. Some are one-method-per-command (`GameObjectHandler.CreateGameObject`), others use an
  `action` parameter dispatched through `HandleCommand(action, params)` (tags, layers, selection,
  windows, asset database…).
- `Helpers/Response.cs` — builds the JSON responses (`Response.SuccessResult(id, data)` /
  `Response.ErrorResult(id, message, code, details)`). The Node side accepts both the old
  (`status`) and new (`success`/`id`) shapes. **Known issue:** the dispatcher wraps handler
  `{ error: … }` results in `SuccessResult`, so domain errors currently arrive as `status:"success"`
  (target contract + fix tracked in `protocol/README.md` → Known deviations).

### Adding a new MCP tool

The command surface is governed by the protocol catalog — edit it first, then implement both halves:

1. Add the command to `protocol/catalog/commands.json` (`name`, `sides`, `params` schema, category).
2. C# handler method in `unity-editor-mcp/Editor/Handlers/` + a `case "tool_name":` in the
   `ProcessCommand` switch in `Editor/Core/UnityEditorMCP.cs`.
3. A `<Name>ToolHandler.js` in `mcp-server/src/handlers/<category>/` with the JSON Schema and a
   carefully written tool description (agent success depends on it — see requirement I1).
4. Import + entry in `HANDLER_CLASSES` in `mcp-server/src/handlers/index.js`.
5. `node protocol/scripts/check-drift.mjs` must pass; add unit tests in
   `mcp-server/tests/unit/handlers/`.

Tool names are `verb_noun` snake_case and must match across the catalog, the JS handler's `name`,
the C# switch case, and the `sendCommand` type string. Keep new Unity API usage guarded per
`COMPATIBILITY.md`.
