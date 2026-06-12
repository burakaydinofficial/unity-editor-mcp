# Compatibility

This project's purpose is to be a **floor-true MCP bridge for old Unity**, not to
race to the newest editor. The engineering rule is **guards, not floors**: every
version-divergent Unity API sits behind a `#if UNITY_X_Y_OR_NEWER` guard with
**both branches maintained**, so support never silently moves.

## Policy

- **Tested floor:** Unity **2020.3 LTS**. The Unity package manifest
  (`unity-editor-mcp/package.json` → `"unity"`) and this document must agree with
  what the code actually compiles against.
- **C# language / runtime:** stay within **C# 8 / netstandard 2.0** (the 2020.3
  Mono reality). Code intended to also compile on 2019.4 is limited to **C# 7.3**.
- **No UI Toolkit editor APIs in core paths** (IMGUI-safe) unless guarded.
- **Node server:** pure JS, no native modules; the protocol tooling
  (`protocol/`) is dependency-free.
- The floor only moves on a **major** version, with a maintained legacy branch.

## Guarded API sites

Every guarded, version-divergent site, so audits are greppable. Keep this table
in sync when you add or remove a guard.

| File : line | Guard | API (newer ⇄ older) | Notes |
| --- | --- | --- | --- |
| `unity-editor-mcp/Editor/Handlers/AssetManagementHandler.cs:13` | `UNITY_2021_2_OR_NEWER` | `UnityEditor.SceneManagement.PrefabStageUtility` ⇄ `UnityEditor.Experimental.SceneManagement.PrefabStageUtility` | `using`-alias; call sites at 743, 756, 814, 873. `StageUtility` (843) is non-experimental in all versions. |
| `unity-editor-mcp/Editor/Handlers/ComponentHandler.cs:509` | `UNITY_6000_0_OR_NEWER` | `Rigidbody.linearDamping` ⇄ `Rigidbody.drag` (write) | |
| `unity-editor-mcp/Editor/Handlers/ComponentHandler.cs:520` | `UNITY_6000_0_OR_NEWER` | `Rigidbody.angularDamping` ⇄ `Rigidbody.angularDrag` (write) | |
| `unity-editor-mcp/Editor/Handlers/ComponentHandler.cs:638` | `UNITY_6000_0_OR_NEWER` | `Rigidbody.linearDamping`/`angularDamping` ⇄ `drag`/`angularDrag` (read) | |
| `unity-editor-mcp/Editor/Handlers/SceneAnalysisHandler.cs:249` | `UNITY_6000_0_OR_NEWER` | `Rigidbody` damping ⇄ drag (read) | |
| `unity-editor-mcp/Editor/Handlers/SceneAnalysisHandler.cs:554` | `UNITY_6000_0_OR_NEWER` | `LightType.Rectangle` ⇄ `LightType.Area` | Area-light enum rename. |
| `unity-editor-mcp/Editor/Tests/ComponentHandlerTests.cs:245` | `UNITY_6000_0_OR_NEWER` | `Rigidbody.linearDamping` ⇄ `drag` | Test mirror of the write guard. |

## Known compatibility / packaging issues

Tracked openly so the list is the work list:

1. **Floor not fully CI-verified.** Two cheap, pure-Node PR gates now guard the
   floor: the **compat-lint** (`scripts/compat-lint.mjs`) flags floor-divergent
   Unity APIs used outside an `#if` guard (it would have caught the PrefabStage
   break), and the **Core `dotnet test`** lane verifies the Unity-independent
   spine. What neither does is actually *compile* the Unity package on each editor:
   a full game-ci matrix on 2020.3 / 2021.3 / 2022.3 / 6000 (requirement A3)
   remains the gold standard — heavyweight (Unity license + minutes), a deliberate
   later step, with local in-editor checks in the interim.
2. **Test Runner wiring — fixed, pending in-editor verification.** Added the
   `UnityEditor.TestRunner` assembly reference to `UnityEditorMCP.Editor.asmdef`,
   declared `com.unity.test-framework` (>= 1.1.33; UPM resolves higher on newer
   editors) in `unity-editor-mcp/package.json`, and added the missing
   `TestRunnerHandler.cs.meta`. Note: the test handler lives in the core editor
   assembly, so the bridge now depends on the test framework (a default package);
   isolating it into its own assembly is a possible later refinement. Confirm
   compilation across the version matrix.
3. **`get_component_types`** is a registered MCP tool with no Unity dispatch
   case — it returns `UNKNOWN_COMMAND` at runtime. Baselined in
   `protocol/catalog/commands.json` → `knownGaps`.

## Adding a guard

1. Wrap the divergent call in `#if UNITY_X_Y_OR_NEWER … #else … #endif` (or a
   `using`-alias for type/namespace moves) with **both** branches implemented.
2. Add a row to the table above (file:line, guard, API, note).
3. Prefer the **oldest-still-correct** API in the `#else` branch so the floor
   keeps working.
4. If it is a common floor-divergent API, add it to `RULES` in
   `scripts/compat-lint.mjs` so unguarded future uses fail CI on every PR.
