# v0.5.0 — The lean Adaptive-Node client

Implements ADR 0006. Goal: the MCP surface is **3 meta-tools**; the agent discovers an instance's
tools on demand and always names the instance it acts on; Node carries no passthrough boilerplate and
no static command-list dependency.

**Decisions (settled):** no default/active instance — `instance` required everywhere, hard error on
missing/ambiguous, even with one editor. On-demand discovery via `list_unity_tools(instance)`
returning tool data (no advertisement / no `list_changed`). Delete the 76 passthrough handlers; keep
the 3 Node-logic tools (`execute_menu_item`, `create_script`, `analyze_screenshot`) but invoke them
through `call_unity_tool` (Node-side interception) so the advertised surface stays the 3 meta-tools.

## Stage 1 — Safety: mandatory instance, remove the default  *(highest value; standalone)*
- `call_unity_tool` + `list_unity_tools`: `instance` becomes **required**; missing/ambiguous →
  hard, clearly-worded error (never a silent default).
- Delete `set_active_unity_instance` (handler + catalog entry + tests). Remove the manager's
  active/default connection (`getActiveConnection`) and `server.js`'s ownership of one active
  `UnityConnection` — handlers resolve a connection from the manager by explicit instance only.
- `list_unity_instances` stays (how the agent learns identifiers).
- Verify: test:ci, drift (catalog now 3 meta-tools + editor commands), dogfood (call with explicit
  instance works; omitted instance errors clearly). Review the commit.

## Stage 2 — Delete the passthrough surface; uniform invocation
- Remove the 76 passthrough `*ToolHandler.js` files + their `index.js` entries.
- Keep the 3 Node-logic handlers; add a Node-side dispatch table in `call_unity_tool`: if `tool` is a
  Node-logic tool → invoke its handler with the resolved connection; else → generic editor passthrough.
- `list_unity_tools(instance)` returns the **union**: the editor manifest's commands + the 3
  Node-logic tool descriptors (so all are discoverable through one path).
- Remove the typed-advertisement machinery (`UNITY_MCP_TYPED_TOOLS`, the typed branch of
  `filterListedTools` — the list is now just the meta-tools).
- Verify: test:ci, dogfood (discover via list_unity_tools; call editor + a Node-logic tool via
  call_unity_tool). Review the commit.

## Stage 3 — Manifest enrichment (field advertisement)
- Editor advertises **result** field hints alongside `params` in its handshake manifest (the
  catalog→`CommandCatalog.g.cs` generator emits result schemas; `Handshake.Commands` carries them).
- `list_unity_tools` surfaces params **and** result-field hints, so the agent learns result shapes on
  demand and drives `fields` projection well. Editor-sourced — no Node→catalog dependency.
- Verify: dotnet (generator/manifest), drift, dogfood (list_unity_tools shows result hints). Review.

## Stage 4 — Re-point the drift gate
- The catalog↔JS-inputSchema check is moot (JS passthroughs gone). Re-point `check-drift.mjs` to
  catalog↔editor-manifest (the C# `CommandCatalog.g.cs` / advertised manifest), so the contract is
  still gated without depending on hand-written JS handlers. Decide the catalog's residual role
  (build-time authoring artifact checked against the C# side).
- Verify: drift passes on the new basis; document in protocol/README.

## Cross-cutting
- Breaking change → v0.5.0 (version bump + publish are the maintainer's release actions).
- Review **every** feature commit (the v0.4.0 lesson — not just fixes).
- Keep the floor green each stage (dotnet, clean 2020.3 compile where C# changes).

## Resume marker
- [x] **Stage 1 — safety** (commit 1bf6908): `instance` required on call_unity_tool + list_unity_tools (new `manager.requireConnection`, hard error on missing/unresolved); `set_active_unity_instance` deleted; META_TOOL_NAMES→3. Active-connection machinery KEPT (passthroughs still use it; removed in Stage 2). 2-agent review → 1 HIGH (requireConnection had no unit tests — added 4 to unityConnectionManager.test.js) + doc-staleness (set_active removed from README/mcp-server-README/setup-guide; counts + the call_unity_tool example fixed; bootstrap CATEGORY map). test:ci 214, drift 81/79/78. Node-side change → verified by unit tests (the live MCP bridge runs a fixed server, can't reload mid-session).
- [x] **Stage 2a — Node-logic routing** (commit ee231c3, additive): core/nodeLogicTools.js (NODE_LOGIC_TOOLS registry + isNodeLogicTool + mergeNodeLogicSurface); call_unity_tool dispatches the 3 to `new Handler(conn).execute()`; list_unity_tools merges the override. Tests: nodeLogicTools.test.js + MetaTools routing cases. test:ci 219, drift 81/79/78.
- [x] **Stage 2b — the big deletion** (commit e089331): deleted 73 passthrough handlers → HANDLER_CLASSES = 3 meta-tools; removed the active connection (server.js createHandlers(manager), lazy/no-startup connect; meta-tool ctors take `manager`; manager dropped getActiveConnection/setActiveInstance/activeOverride/activeTarget/getConnectionForInstance/ACTIVE_KEY + prune special-case; ListUnityInstances active-marking gone); removed UNITY_MCP_TYPED_TOOLS (deleted toolExposure.js). Drift re-point: dropped "server" from the 76 editor commands' catalog sides (atomic with the deletion — the reverse check forbids re-pointing first), regenerated CommandCatalog.g.cs → catalog 81 / server 3 / editor 78. Deleted ~57 dead test files (passthrough unit tests + old tools/ + integration); rewrote server + ListUnityInstances tests; trimmed manager test. Gates: test:ci 133, full unit 280, dotnet 127, drift 81/3/78, compat-lint OK. NOTE: BaseToolHandler was briefly deleted by an over-broad find glob (`*ToolHandler.js` matched it) — restored from git; watch that glob.
- [x] **Stage 2b review** (3-agent wf_190280b8): MEDIUM = orphaned `src/tools/` (11 dead files incl. a ListTools registration footgun — I'd deleted tests/unit/tools/ but not the src layer) → deleted; LOW = dead `src/utils/validators.js` + 2 duplicate test files → deleted; LOW = README still mentioned `UNITY_MCP_TYPED_TOOLS` → fixed; LOW = catalog `list_unity_instances` result schema still had `activePort`/`active` → removed. No correctness defects in the lean surface itself.
- [x] **Stage 3 — result-field advertisement** (commit 891303a): generator emits `result` into CommandManifestJson (regen, ~118KB const); editorToolSurface carries it; list_unity_tools(name) returns {…, result}; catalog synced. **FLOOR-DOGFOODED**: forced a recompile on the live 2020.3 — UnityEditorMCP.Core.dll compiled clean (0 messages), so the 118KB verbatim-string const is valid on the oldest Mono floor. test:ci 135, dotnet 127, drift 81/3/78. 2-agent review wf_f8960eed pending.
- [x] **Stage 4 — docs** (commit 3bf9c1b): README reframed (3-tool surface + editor capability catalog + result hints), CHANGELOG [0.5.0], CLAUDE.md + setup-guide dropped UNITY_MCP_TYPED_TOOLS. ADR 0006 → Accepted.

**v0.5.0 COMPLETE** (Stages 1–4 implemented + reviewed; Stage 3 review pending). Surface = 3 meta-tools; instance required; editor-advertised params+result hints. 142 ahead. Maintainer ship-prep (NOT done here): version bump (still 0.3.0) + tag + npm publish.
