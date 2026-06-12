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

1. **Floor not yet CI-verified.** There is no CI matrix compiling the Unity
   package on 2020.3 / 2021.3 / 2022.3 / 6000. The PrefabStage floor-break
   (the `AssetManagementHandler` guard above) reached `main` precisely because
   nothing compiled the C# on the claimed floor. Standing up that matrix
   (requirement A3) is the single highest-leverage compatibility task.
2. **Test Runner integration is under-wired.** `TestRunnerHandler.cs` uses
   `UnityEditor.TestTools.TestRunner.Api`, but the editor asmdef
   (`UnityEditorMCP.Editor.asmdef`) references only `Newtonsoft.Json` and
   `unity-editor-mcp/package.json` does not declare `com.unity.test-framework`
   as a dependency. Also, `TestRunnerHandler.cs` is **missing its `.meta` file**
   (the only source file in the package without one). Verify in-editor.
3. **`get_component_types`** is a registered MCP tool with no Unity dispatch
   case — it returns `UNKNOWN_COMMAND` at runtime. Baselined in
   `protocol/catalog/commands.json` → `knownGaps`.

## Adding a guard

1. Wrap the divergent call in `#if UNITY_X_Y_OR_NEWER … #else … #endif` (or a
   `using`-alias for type/namespace moves) with **both** branches implemented.
2. Add a row to the table above (file:line, guard, API, note).
3. Prefer the **oldest-still-correct** API in the `#else` branch so the floor
   keeps working.
