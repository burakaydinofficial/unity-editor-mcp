# Asset & Prefab Lifecycle + Destructive-Op Safety (0.10.0) Plan

> REQUIRED SUB-SKILL: superpowers:executing-plans. Spec: `docs/superpowers/specs/2026-06-21-asset-prefab-lifecycle-design.md`.
> Conventions (from the inherited asset handlers): `HandlerOutcome.Ok(new { success=true, … })` anonymous-object
> payloads; `HandlerOutcome.Fail(message, code)`; `PathSafety.IsWithinProject`; `GetGameObjectPath` helper.

**Verification loop:** `refresh_assets` → `read-editor-log.mjs` → `run_tests EditMode` → `get_test_results`; `compat-lint`; `check-drift`.

---

## Task 1: three new commands + delete-gate + scene guards (one editor change set)

**Files:** `AssetManagementHandler.cs` (3 new methods), `AssetDatabaseHandler.cs` (DeleteAsset gate + FindDependents), `SceneHandler.cs` (CreateScene/SaveScene guards), `UnityEditorMCP.cs` (3 registrations), `protocol/catalog/commands.json` (3 entries).

- [ ] **`AssetManagementHandler.CreateScriptableObject`** — resolve type via `ManagedReferenceResolver.ResolveType`; verify `typeof(ScriptableObject).IsAssignableFrom(type)` (else `NOT_A_SCRIPTABLE_OBJECT`); path validation (`Assets/…​.asset`, `PathSafety`); `PATH_EXISTS` unless `overwrite`; `ScriptableObject.CreateInstance(type)` → `AssetDatabase.CreateAsset` → `SaveAssets`; return `{ assetPath, guid, type }`.
- [ ] **`AssetManagementHandler.UnpackPrefab`** — play-mode guard; resolve GO by `instanceId`/`gameObjectPath`; `PrefabUtility.IsPartOfPrefabInstance` (else `NOT_A_PREFAB_INSTANCE`); `PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.UserAction)` (mode `regular`→`OutermostRoot`, `complete`→`Completely`; UserAction registers undo).
- [ ] **`AssetManagementHandler.CreatePrefabVariant`** — resolve base by `basePrefabPath`/`baseGuid`; `variantPath` validation + `PATH_EXISTS`; `InstantiatePrefab(base)` → `PrefabUtility.CreatePrefabVariant(instance, variantPath)` → `DestroyImmediate(instance)` in finally → `SaveAssets`.
- [ ] **`AssetDatabaseHandler.DeleteAsset(assetPath, confirm)`** + `FindDependents` (reverse scan over `GetAllAssetPaths` + `GetDependencies(p,false)`): without `confirm` → `{ confirmRequired:true, wouldDelete, dependents, dependentCount }` (no delete); with `confirm:true` → delete + `{ deleted, hadDependents }`. Update the `delete_asset` case to read `parameters["confirm"]`.
- [ ] **`SceneHandler.CreateScene` + `SaveScene`** — first line in `try`: `if (EditorApplication.isPlaying) return HandlerOutcome.Fail("scene mutations refuse in play mode", "PLAY_MODE");` (LoadScene intentionally NOT guarded — it branches play/edit).
- [ ] **Register** in `BuildDispatcher`: `create_scriptable_object`→CreateScriptableObject, `unpack_prefab`→UnpackPrefab, `create_prefab_variant`→CreatePrefabVariant.
- [ ] **Catalog** (category `asset`, `sides:["editor"]`): `create_scriptable_object` (destructive false), `unpack_prefab` (destructive true), `create_prefab_variant` (destructive false). Regen + drift.
- [ ] Recompile → compile clean; `compat-lint` OK; `check-drift` OK.

## Task 2: tests + floor dogfood

**Files:** `Tests/Editor/Handlers/AssetLifecycleTests.cs` (new).

- [ ] Tests (call handler methods directly with JObject; clean up assets in TearDown):
  - `CreateScriptableObject_CreatesAsset` (type `UnityEditorMCP.Tests.SerFixtureAsset` → asset loads + is SerFixtureAsset); `_NotAScriptableObject` (`System.String`→`NOT_A_SCRIPTABLE_OBJECT`); `_TypeNotFound`; `_PathExists` (no overwrite).
  - `UnpackPrefab_RemovesInstanceLink` (SaveAsPrefabAsset + InstantiatePrefab → unpack → `!IsPartOfPrefabInstance`); `_NotAPrefabInstance` (plain GO).
  - `CreatePrefabVariant_ChainsToBase` (base prefab → variant → `GetCorrespondingObjectFromSource` of the variant root's instance chains to base); `_PathExists`.
  - `DeleteAsset_RequiresConfirm` (no confirm → `confirmRequired` true + asset still exists); `DeleteAsset_WithConfirm_Deletes`.
- [ ] Recompile → EditMode all green. Floor dogfood: `create_scriptable_object`, `unpack_prefab`, `create_prefab_variant`, a `manage_asset_database` delete dry-run, over the bridge.
- [ ] Commit.

## Self-review
- Spec coverage: E1 (create SO + delete-gate), E2 (unpack + variant), E6 (scene guards). Deferred items unchanged. Error codes NOT_A_SCRIPTABLE_OBJECT/NOT_A_PREFAB_INSTANCE/PATH_EXISTS/PLAY_MODE/confirmRequired.
- Floor-safe: ScriptableObject.CreateInstance(Type), PrefabUtility.UnpackPrefabInstance/CreatePrefabVariant (2018.3+), EditorApplication.isPlaying. No new COMPATIBILITY.md entry.
- Reuse: ManagedReferenceResolver.ResolveType (0.9), GetGameObjectPath/PathSafety (existing).
