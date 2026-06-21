# Missing-Script Detection (0.16.0) Design

> Status: design (autonomous). Requirement **F5**: "Missing-script detection (a legacy-project staple): report
> missing MonoBehaviours per scene/prefab, optional cleanup with a confirm flag." Squarely on the fork's mission
> — old projects accumulate missing scripts (a `.cs` deleted/moved leaves a dangling component).

## 1. Scope

Two commands over the **active scene** (the common case; prefab-asset scope is a noted follow-up):
- **`find_missing_scripts`** (read-only): scan the active scene, report each GameObject that has ≥1
  missing-script MonoBehaviour, as `{ path, missingCount }`. Result `{ objects:[…], totalObjects, totalMissing,
  truncated, limit }` (F2-style cap so a badly-broken scene can't blow the frame budget).
- **`remove_missing_scripts`** (destructive): remove the missing-script components from the active scene —
  all, or a targeted `gameObjectPaths[]`. Registers Undo, marks the scene dirty, refuses in play mode
  (`PLAY_MODE`). Result `{ removed, objectsAffected }`. Marked **`requiresConfirm`** so it rides the central H3
  gate — you confirm before mutating the scene.

**Deferred:** prefab-asset scope (load/modify/save each prefab) and multi-scene; noted, not built here.

## 2. Implementation

New `MissingScriptHandler` (Editor/Handlers). Scene walk: `SceneManager.GetActiveScene().GetRootGameObjects()`
→ each root's `GetComponentsInChildren<Transform>(true)` (includes inactive) → the GameObject set. Per object:
`GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go)` (the count). Removal:
`GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go)` after `Undo.RegisterCompleteObjectUndo(go, …)`;
sum the returned counts; `EditorSceneManager.MarkSceneDirty` on the active scene if anything was removed.
`GameObject.Find`-style path resolution for `gameObjectPaths` (with a `notFound` list, like delete_gameobject).

## 3. Error model & floor-safety

`PLAY_MODE` on `remove_missing_scripts`; `CONFIRMATION_REQUIRED` via the central gate. All APIs are floor-safe
(2020.3): `GameObjectUtility.GetMonoBehavioursWithMissingScriptCount` / `RemoveMonoBehavioursWithMissingScript`
(2019.1+), `GetComponentsInChildren(true)`, `Undo.RegisterCompleteObjectUndo`, `EditorSceneManager.MarkSceneDirty`.
Nothing for COMPATIBILITY.md.

## 4. Catalog & integration

`find_missing_scripts` (category `analysis`, read-only) + `remove_missing_scripts` (category `gameobject`,
`destructive:true`); both `sides:["editor"]`. Registered in `BuildDispatcher`; `remove_missing_scripts` with
`requiresConfirm:true`. Manifest regenerated, drift green.

## 5. Testing

- **EditMode:** `find_missing_scripts` on a clean fixture hierarchy → `totalMissing == 0` + the structured shape
  (objects array, totals); `remove_missing_scripts` with nothing missing → `removed == 0`, not an error;
  `remove_missing_scripts` with a bogus `gameObjectPaths` entry → reported in `notFound`. (The POSITIVE path —
  an actual dangling script — can't be fabricated cleanly in EditMode; it relies on Unity's well-established
  `GetMonoBehavioursWithMissingScriptCount`/`RemoveMonoBehavioursWithMissingScript`, so the wrapper logic
  — scan / report / Undo / gate — is what's tested. Documented limitation.)
- **Dogfood (floor):** `find_missing_scripts` on the live scene → structured result; `remove_missing_scripts`
  without confirm → `CONFIRMATION_REQUIRED` (the gate), confirming the registration.
- **Gates:** drift, compat-lint, Core dotnet, EditMode.
