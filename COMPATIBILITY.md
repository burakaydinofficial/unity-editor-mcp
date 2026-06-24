# Compatibility

This project's purpose is to be a **floor-true MCP bridge for old Unity**, not to
race to the newest editor. The engineering rule is **guards, not floors**: every
version-divergent Unity API sits behind a `#if UNITY_X_Y_OR_NEWER` guard with
**both branches maintained**, so support never silently moves.

## Policy

- **Tested floor:** Unity **2019.4 LTS** — the lowest version in the floor-matrix CI
  (2019.4 / 2020.3 / 2021.3 / 2022.3, EditMode green on each). The Unity package
  manifest (`unity-editor-mcp/package.json` → `"unity"` = `2019.4`) and this document
  must agree with what the code actually compiles against.
- **C# language / runtime:** stay within **C# 7.3 / netstandard 2.0** (the 2019.4
  floor's Mono — older than 2020.3's C# 8). This also rules out **netstandard 2.1+
  BCL APIs** — e.g. `string.Contains(value, StringComparison)` and the `char`
  `Contains`/`StartsWith`/`EndsWith` overloads (use `IndexOf(value, StringComparison)
  >= 0` instead). The compat-lint guards the curated cases; the rest surface on the
  floor-matrix cold compile.
- **No UI Toolkit editor APIs in core paths** (IMGUI-safe) unless guarded.
- **Node server:** pure JS, no native modules; the protocol tooling
  (`protocol/`) is dependency-free.
- The floor only moves on a **major** version, with a maintained legacy branch.

## Guarded API sites

Every guarded, version-divergent site, so audits are greppable. Keep this table
in sync when you add or remove a guard.

| File : line | Guard | API (newer ⇄ older) | Notes |
| --- | --- | --- | --- |
| `unity-editor-mcp/Editor/Handlers/AssetManagementHandler.cs:14` | `UNITY_2021_2_OR_NEWER` | `UnityEditor.SceneManagement.PrefabStageUtility` ⇄ `UnityEditor.Experimental.SceneManagement.PrefabStageUtility` | `using`-alias; call sites at 744, 760, 818, 877. `StageUtility` (847) is non-experimental in all versions. |
| `unity-editor-mcp/Editor/Handlers/ComponentHandler.cs:607` | `UNITY_6000_0_OR_NEWER` | `Rigidbody.linearDamping` ⇄ `Rigidbody.drag` (write) | |
| `unity-editor-mcp/Editor/Handlers/ComponentHandler.cs:618` | `UNITY_6000_0_OR_NEWER` | `Rigidbody.angularDamping` ⇄ `Rigidbody.angularDrag` (write) | |
| `unity-editor-mcp/Editor/Handlers/ComponentHandler.cs:736` | `UNITY_6000_0_OR_NEWER` | `Rigidbody.linearDamping`/`angularDamping` ⇄ `drag`/`angularDrag` (read) | |
| `unity-editor-mcp/Editor/Handlers/SceneAnalysisHandler.cs:250` | `UNITY_6000_0_OR_NEWER` | `Rigidbody` damping ⇄ drag (read) | |
| `unity-editor-mcp/Editor/Handlers/SceneAnalysisHandler.cs:555` | `UNITY_6000_0_OR_NEWER` | `LightType.Rectangle` ⇄ `LightType.Area` | Area-light enum rename. |
| `unity-editor-mcp/Tests/Editor/Handlers/ComponentHandlerTests.cs:239` | `UNITY_6000_0_OR_NEWER` | `Rigidbody.linearDamping` ⇄ `drag` | Test mirror of the write guard. |
| `unity-editor-mcp/Editor/Handlers/AssetManagementHandler.cs` (`PrefabStageAssetPath`) | `UNITY_2020_1_OR_NEWER` | `PrefabStage.assetPath` ⇄ `PrefabStage.prefabAssetPath` | Path of the prefab being edited; centralized in the `PrefabStageAssetPath` helper (call sites in open/exit/save prefab). Also adds a guarded `PrefabStage` type alias next to `PrefabStageUtility`. Caught by a 2019.4 cold compile (CS1061). |
| `unity-editor-mcp/Editor/Handlers/GameObjectHandler.cs` (`FindGameObjects`) | `UNITY_2020_1_OR_NEWER` | `FindObjectsOfType<T>(includeInactive)` ⇄ `Resources.FindObjectsOfTypeAll<T>()` (scene-filtered) | The `includeInactive` overload is 2020.1+. Caught by a 2019.4 cold compile (CS1501). |

## Known compatibility / packaging issues

Tracked openly so the list is the work list:

1. **Floor CI-verified (2019.4–2022.3); Unity 6 pending.** The **floor-matrix**
   (`.github/workflows/floor-matrix.yml`) cold-compiles the package and runs the full
   EditMode suite on **2019.4 / 2020.3 / 2021.3 / 2022.3** (GameCI, per-version host
   projects under `ci/unity-host-<ver>/`) on every release tag and on PRs touching the
   package. Backed by two pure-Node PR gates: **compat-lint** (`scripts/compat-lint.mjs`,
   flags unguarded floor-divergent APIs) and the **Core `dotnet test`** lane. Still
   open: a **Unity 6 (6000.x)** host in the matrix (the API is guarded; the host
   project is pending). Lesson: floor-compat needs a *cold* compile — an interactive
   editor's incremental compile can hide a floor break with a stale assembly.
2. **Test Runner wiring — fixed and verified live.** Added the
   `UnityEditor.TestRunner` assembly reference to `UnityEditorMCP.Editor.asmdef`,
   declared `com.unity.test-framework` (>= 1.1.33; UPM resolves higher on newer
   editors) in `unity-editor-mcp/package.json`, and added the missing
   `TestRunnerHandler.cs.meta`. Note: the test handler lives in the core editor
   assembly, so the bridge now depends on the test framework (a default package);
   isolating it into its own assembly is a possible later refinement. Verified live:
   the EditMode suite (71 tests) compiles and passes on **2020.3, 2021.3, and
   2022.3**, each driven through the bridge (see `docs/floor-testing.md`).
3. **`get_component_types`** is now implemented on the Core `CommandDispatcher` rail
   (`ComponentHandler.GetComponentTypes`); `protocol/catalog/commands.json` →
   `knownGaps` is empty and the drift gate reports zero gaps. (Previously a registered
   MCP tool with no Unity dispatch case that returned `UNKNOWN_COMMAND` at runtime.)
4. **`Gradient` read/write via reflection (serialization core, 0.9.0).**
   `SerializedProperty.gradientValue` is `internal` until Unity 2022.2, so
   `SerializedValue.GradientReflection` accesses it by reflection — a single code path
   that works on the 2020.3 floor and newer. This is a deliberate **reflection
   workaround, not an `#if` floor break**: if a future Unity renames the internal
   property, read returns `null` and write returns a structured `TYPE_MISMATCH` (never
   a crash). The `[SerializeReference]` APIs it sits beside — `managedReferenceValue`
   (2019.3+), `managedReferenceFullTypename` (2019.3+), `managedReferenceFieldTypename`
   (2020.1+) — are all available on the 2020.3 floor, so no guard is needed.

5. **Floor verified across 2019.4–2022.3 (the fork's "floor-true" mission, made real).** Cold batch + CI compiles
   surfaced floor breaks the interactive editor's incremental compile had masked, now resolved:
   - **2019.4:** `loadedSceneCount` (avoided — manual count via `sceneCount`/`GetSceneAt`/`isLoaded`),
     `PrefabStage.assetPath` + `FindObjectsOfType<T>(includeInactive)` (guarded `UNITY_2020_1_OR_NEWER`, see the
     table), and `AnimationWindow` (`internal` on 2019.4 — referenced by full type name via reflection in
     `ToolManagementHandler`, version-agnostic, no `#if`).
   - **2022.3:** `get_object_references` missed `Joint.m_ConnectedBody` — hidden from the Inspector by a custom
     editor on 2022.3+, so the `NextVisible` reference scan skipped it; switched the walk to `Next` (every
     serialized property, regardless of visibility).
   2019.4 is now the package's declared floor (`unity` = `2019.4`) and in the floor-matrix CI (EditMode green on
   all four). Lesson: floor-compat needs a **cold** compile — an interactive editor's incremental compile can reuse
   a stale assembly and hide a floor break.

6. **`reorder_component` is a no-op in some older editors' batch mode (Unity limitation, not ours).** It calls
   `ComponentUtility.MoveComponentUp/Down`, which returns `false` (no movement) in **2020.3 batch mode** — so
   `reorder_component` reports `moved: 0`. Verified live: it works on **2021.3 / 2022.3** (the component order
   genuinely changes — `[Transform, Rigidbody, BoxCollider]` → `[Transform, BoxCollider, Rigidbody]`). The handler
   now says so in its `moved: 0` message. No public batch-safe alternative exists (component order is
   engine-internal), so this is documented rather than worked around.

## Adding a guard

1. Wrap the divergent call in `#if UNITY_X_Y_OR_NEWER … #else … #endif` (or a
   `using`-alias for type/namespace moves) with **both** branches implemented.
2. Add a row to the table above (file:line, guard, API, note).
3. Prefer the **oldest-still-correct** API in the `#else` branch so the floor
   keeps working.
4. If it is a common floor-divergent API, add it to `RULES` in
   `scripts/compat-lint.mjs` so unguarded future uses fail CI on every PR.
