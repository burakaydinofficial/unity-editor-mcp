# Asset & Prefab Lifecycle + Destructive-Op Safety (0.10.0) Design

> Status: design (autonomous). First slice of requirements **section E** (Asset & Prefab Operations). Section E
> is ~70% inherited from the base fork and working (prefab create/open/instantiate/save, asset DB ops, import
> settings, dependency queries, scene open/save/new — all on the `HandlerOutcome` contract). This slice closes
> the genuine **P0 gaps** and the two destructive-safety holes a capability audit surfaced.

## 1. Scope

**In 0.10.0:**
- **`create_scriptable_object` (E1)** — instantiate a `ScriptableObject`-derived type by name and save it as an
  asset. The one missing piece of the asset-creation lifecycle (material + prefab create already exist).
- **`unpack_prefab` (E2)** — `PrefabUtility.UnpackPrefabInstance` (regular = outermost-root / complete).
- **`create_prefab_variant` (E2)** — `PrefabUtility.CreatePrefabVariant` from a base prefab.
- **Destructive-delete safety (E1 + H3 down-payment)** — the `manage_asset_database` `delete` action gains a
  `confirm:true` gate and returns the asset's **dependents** so an agent never blind-deletes something still
  referenced.
- **Scene play-mode guards (E6)** — `create_scene` / `save_scene` / `load_scene` refuse with a structured
  `PLAY_MODE` error while the editor is playing (currently unguarded).

**Deferred (later E slices / other sections):** granular per-property prefab override apply (E2 — needs the
serialization propertyPath, a focused follow-up), build-settings scene list read/modify (E6 P1), platform
import overrides (E4 P2), dependency-query paging (E5 P1), and the *systematic* H3 confirm-gate across **all**
destructive commands (section H / 0.11 — this slice only hardens the asset-delete it touches).

## 2. `create_scriptable_object` (E1)

- **Params:** `typeName` (FullName/AQN of a `ScriptableObject` subtype), `assetPath` (`Assets/…​.asset`),
  optional `overwrite` (default false).
- **Behavior:** resolve the type with the existing **`ManagedReferenceResolver.ResolveType`** (reuse from 0.9 —
  the same FullName resolution the agent already uses with `find_implementations`); verify it derives from
  `ScriptableObject` (else `NOT_A_SCRIPTABLE_OBJECT`); `ScriptableObject.CreateInstance(type)`;
  `AssetDatabase.CreateAsset` at `assetPath` (refuse an existing path unless `overwrite`); `SaveAssets`.
- **Result:** `{ guid, path, type }`. Errors: `TYPE_NOT_FOUND`, `NOT_A_SCRIPTABLE_OBJECT`, `PATH_EXISTS`,
  `VALIDATION_ERROR`. Pairs with `set_serialized_properties` — create the SO, then populate its fields (incl.
  private `[SerializeField]`) by the serialization core.

## 3. `unpack_prefab` (E2)

- **Params:** `target` (a scene prefab-instance, by `scenePath`/`instanceId`), `mode` (`"regular"` →
  `PrefabUnpackMode.OutermostRoot`, `"complete"` → `Completely`; default `regular`).
- **Behavior:** resolve the GameObject; verify it is a prefab instance (`PrefabUtility.IsPartOfPrefabInstance`,
  else `NOT_A_PREFAB_INSTANCE`); refuse in play mode (`PLAY_MODE`); `Undo.RegisterFullObjectHierarchyUndo` then
  `PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.UserAction)`.
- **Result:** `{ unpacked: scenePath, mode }`.

## 4. `create_prefab_variant` (E2)

- **Params:** `basePrefabPath` (or `baseGuid`), `variantPath` (`Assets/…​.prefab`), optional `overwrite`.
- **Behavior:** load the base prefab asset (`TARGET_NOT_FOUND` if absent); refuse existing `variantPath` unless
  `overwrite` (`PATH_EXISTS`); instantiate it (`PrefabUtility.InstantiatePrefab`), call
  `PrefabUtility.CreatePrefabVariant(instance, variantPath)`, then `DestroyImmediate` the temp instance.
- **Result:** `{ guid, path, base }`.

## 5. Destructive-delete safety (E1 + H3)

The `manage_asset_database` `delete` action (in `AssetDatabaseHandler`) is currently blind. Harden:
- Compute **dependents** (reuse `AssetDependencyHandler`'s reverse-scan) before deleting.
- **Require `confirm: true`.** Without it, do **not** delete — return `{ wouldDelete: path, dependents: [...],
  confirmRequired: true }` (a dry-run preview). With `confirm: true`, delete via `AssetDatabase.DeleteAsset`
  and return `{ deleted: path, hadDependents: n }`.
- This is the "destructive ops require an explicit `confirm`, dry-run by default" rule (H3), applied to the
  asset-delete this slice touches. The broader systematic gate over every destructive command is 0.11 (section H).

## 6. Scene play-mode guards (E6)

`create_scene` / `save_scene` / `load_scene` (single-scene load that swaps the edit scene) currently mutate
without checking play mode. Add an early `if (EditorApplication.isPlaying) return Fail("scene mutations refuse
in play mode", "PLAY_MODE")` — matching the serialization core's scene-write guard and the Inspector UX
(these are greyed out in play mode). Read-only scene queries (`get_scene_info`, `list_scenes`) are unaffected.

## 7. Error model

Adds `NOT_A_SCRIPTABLE_OBJECT`, `NOT_A_PREFAB_INSTANCE`, `PATH_EXISTS`, `confirmRequired` to the established set
(`TYPE_NOT_FOUND`, `TARGET_NOT_FOUND`, `PLAY_MODE`, `VALIDATION_ERROR`, …), all via `HandlerOutcome.Fail(message,
code)`. Consistent with the serialization core's structured-error contract.

## 8. Floor-safety (2020.3 / C# 8 / netstandard 2.0)

All APIs are on the floor: `ScriptableObject.CreateInstance(Type)`, `AssetDatabase.CreateAsset`/`DeleteAsset`,
`PrefabUtility.UnpackPrefabInstance` + `PrefabUnpackMode` (2018.3+), `PrefabUtility.CreatePrefabVariant`
(2018.3+), `PrefabUtility.InstantiatePrefab`/`IsPartOfPrefabInstance`, `EditorApplication.isPlaying`,
`Undo.RegisterFullObjectHierarchyUndo`. No version-divergent API; nothing new for COMPATIBILITY.md.

## 9. Catalog & integration

Three new catalog entries (`create_scriptable_object`, `unpack_prefab`, `create_prefab_variant`; category
`asset`, `sides:["editor"]`; the latter two + create marked `destructive` appropriately), registered on the
dispatcher in `BuildDispatcher`. The delete-gate + scene-guards are behavior changes to existing commands (the
catalog `manage_asset_database` description gains the `confirm`/dependents note). Manifest regenerated, drift gate
green.

## 10. Testing (NUnit EditMode)

- **create_scriptable_object:** create a `SerFixtureAsset` (the serialization fixture is a ScriptableObject) by
  type name → asset exists at path + loads + is the right type; `NOT_A_SCRIPTABLE_OBJECT` (a non-SO type);
  `TYPE_NOT_FOUND`; `PATH_EXISTS` without overwrite; then populate a field via `set_serialized_properties`
  (the create→populate pairing).
- **unpack_prefab:** create a prefab + instance → unpack (regular) → `IsPartOfPrefabInstance` is now false;
  `NOT_A_PREFAB_INSTANCE` on a plain GameObject.
- **create_prefab_variant:** from a base prefab → variant asset exists + `GetCorrespondingObjectFromSource`
  chains to the base; `PATH_EXISTS`.
- **delete gate:** delete without `confirm` → not deleted + `confirmRequired` + dependents listed; with
  `confirm:true` → deleted.
- **scene guards:** assert the `PLAY_MODE` code path (unit-level: the guard returns Fail when `isPlaying` — test
  via the guard helper, since EditMode tests don't enter play mode).
- **Floor:** dogfood on 2020.3 — create a ScriptableObject, unpack a prefab instance, create a variant, and a
  delete dry-run, over the bridge.
