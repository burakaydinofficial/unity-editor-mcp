# v0.4.0 — Dispatch-rail migration (master the existing structure)

**Goal.** Move all 76 legacy `ProcessCommand` switch cases onto the tested Core
`CommandDispatcher` rail (the `HandlerOutcome`/`CommandResult` contract), then retire the
legacy switch to a thin fallback and enforce result schemas. Today only 2 of 78 commands
(`handshake`, `get_component_types`) ride the rail; ~97% of dispatch bypasses the spine the
dotnet suite verifies.

**In scope (v0.4.0):** the migration; result-schema enforcement at the Node boundary; a
surface-consistency pass (uniform error codes / response shapes / validation); docs truth
(README + catalog reflect 78 tools + the `code` category) + CHANGELOG.
**Out of scope:** floor-CI matrix + release automation (separate ship-prep, needs secrets);
SDK bump; the lean-client refactor (0.5.0); Roslyn (0.6.0).

## The rail contract (already in `Core/`)
- `HandlerOutcome.Ok(object payload)` / `HandlerOutcome.Fail(string error, string code = "INTERNAL_ERROR", string remediation = null, object details = null)` — structurally cannot serialize an error as success.
- `CommandDispatcher.Register(type, Func<JObject, HandlerOutcome>)`; `Dispatch` wraps in try/catch → `CommandResult` → envelope. Unknown/throwing handlers already yield proper errors.
- Wiring: `BuildDispatcher()` (UnityEditorMCP.cs) registers handlers; `ProcessCommandQueue` routes `IsRegistered(type)` → `DispatchViaCore`, else the legacy `ProcessCommand` switch (the strangler fallback).

## Migration template

### Shape A — single-method handler (e.g. `SceneAnalysisHandler.GetObjectReferences`)
Before (handler returns raw `JObject`; switch classifies via `Response.Result`):
```csharp
public static JObject GetObjectReferences(JObject p) { ...; return new JObject{...}; }   // success
// errors: return new JObject { ["error"] = msg };
// switch: case "get_object_references": response = Response.Result(id, Handler.GetObjectReferences(p)); break;
```
After (handler returns `HandlerOutcome`; registered on the rail):
```csharp
public static HandlerOutcome GetObjectReferences(JObject p) { ...; return HandlerOutcome.Ok(new JObject{...}); }
// errors: return HandlerOutcome.Fail(msg, "VALIDATION_ERROR");   // code per the table below
// BuildDispatcher: dispatcher.Register("get_object_references", SceneAnalysisHandler.GetObjectReferences);
// switch: delete the case.
```

### Shape B — action-dispatch handler (`HandleCommand(...)` serving multiple commands)
`HandleCommand` returns `HandlerOutcome`; register each command type with a thin lambda:
```csharp
// PlayModeHandler.HandleCommand(string command, JObject p) -> HandlerOutcome
dispatcher.Register("play_game",       p => PlayModeHandler.HandleCommand("play_game", p));
dispatcher.Register("pause_game",      p => PlayModeHandler.HandleCommand("pause_game", p));
// manage_* style (action in params): one registration reading p["action"]:
dispatcher.Register("manage_asset_database", p => AssetDatabaseHandler.HandleCommand(p["action"]?.ToString(), p));
```
Internal `CreateSuccessResponse`/`CreateErrorResponse` helpers become `HandlerOutcome.Ok`/`Fail`.

### Error-code convention (consistency pass)
Codes come from `protocol/schemas/error-codes.json`. Map each error site:
- missing/invalid params → `VALIDATION_ERROR`
- target object/asset/component not found → `NOT_FOUND`
- operation not valid in current editor state → `INVALID_STATE`
- caught exception / unexpected → `INTERNAL_ERROR` (the `Fail` default)

## Batch order (low-risk → high; ~10 batches by category)
1. **Pilot: PlayMode** (4 cmds) — proves Shape B end-to-end; only `get_editor_state` smoke-tested live (play/pause/stop are disruptive).
2. System / console (ping, read_logs, refresh_assets, clear/enhanced logs)
3. Editor & project ops (the new EditorInfoHandler set — already HandlerOutcome-friendly) + tags/layers/selection/windows/tools
4. GameObject + hierarchy
5. Scene + scene analysis
6. Component
7. Asset (prefab/material/db/import/dependency) — largest
8. Scripting + compilation
9. UI interaction
10. Test runner + screenshot + code-intel
Each command keeps identical wire behavior (the catalog + manifest are unchanged → **drift unaffected**).

## Per-batch verification protocol
- `node protocol/scripts/check-drift.mjs` (unchanged surface → stays green), `node scripts/compat-lint.mjs`.
- Floor dogfood: `refresh_assets` → `read-editor-log.mjs` (compile clean) → smoke one **read-only** command per batch via `call_unity_tool` (proves it routes through the rail now that its switch case is gone).
- `cd mcp-server && npm run test:ci` + `dotnet test` (dotnet only catches Core changes; editor handlers rest on dogfood).
- Commit per batch (small, reversible). Never move the `v0.3.0` tag.

## Capstone (after all 76 registered)
1. Delete the legacy `ProcessCommand` switch body; keep `DispatchViaCore` as the sole path (or a `SetFallback` that returns `UNKNOWN_COMMAND`). Verify `_dispatcher.Count` == editor command count.
2. **Result-schema enforcement**: validate handler payloads against the catalog `result` schemas at the Node boundary (closes protocol/README "derived, not enforced"). Start as warn, then enforce.
3. Docs truth (README/mcp-server-README catalog → 78 tools + `code` category) + CHANGELOG (0.3.0→0.4.0).
4. Final adversarial swarm over the whole migration delta; fix to convergence.

## Resume marker
Progress tracked in memory `v030-execution-progress.md`. Batches completed:
- [x] **1. PlayMode** (play_game/pause_game/stop_game/get_editor_state) — Shape B proven; get_editor_state dogfooded live. Rail 2→6.
- [x] **2. Editor & project ops** (EditorInfoHandler: get_editor_info/get_project_settings/list_packages/set_project_setting/manage_packages/quit_editor) — Shape A; get_editor_info + list_packages dogfooded live through the rail. Rail 6→12. (Done ahead of the system/console batch — these were the cleanest handlers, written this session.)
- [x] **A. 10 single-method handlers** (agent-converted, parallel): GameObject(5), Scene(5), SceneAnalysis(5), CodeIntelligence(4), UIInteraction(5), Component(4: add/remove/modify/list), Compilation(3), Screenshot(2), Menu(1), Console(2) = 36 cmds. Rail 12→48. 3-agent review caught 2 faithfulness issues (Flatten added a `code` field; ScreenshotHandler dropped a `note`) — both fixed. Dogfooded get_hierarchy/list_scenes/get_compilation_state/analyze_scene_contents through the rail. NOTE: from batch A on, dead switch cases are LEFT in place (rail wins via IsRegistered; Response.Result(object) so they compile) and the whole switch is deleted at the capstone — avoids 36 scattered case-deletions.
- [ ] **B. action-dispatch + remaining**: the 4 inline (ping/read_logs/clear_logs/refresh_assets → BuildDispatcher lambdas or a SystemHandler), the 8 manage_* HandleCommand(action,params) handlers (TagManagement/LayerManagement/Selection/WindowManagement/ToolManagement/AssetImportSettings/AssetDatabase/AssetDependency), AssetManagementHandler(8 single-method), ScriptHandler(6), TestRunnerHandler(4).
- [ ] **Capstone**: delete the legacy ProcessCommand switch; result-schema enforcement; docs/CHANGELOG; 10-agent sweep over the whole migration (after batch B = ~4 batches in).
