# Granular Prefab-Override Apply/Revert (0.20.0, E-tail slice 3) Design

> Status: design (autonomous). Section **E** tail, slice 3. `save_prefab` applies prefab-instance overrides
> all-or-nothing (`ApplyPrefabInstance` / root `ApplyObjectOverride`). There is no way to **inspect** what is
> overridden on an instance, or to **apply/revert a single property** — the everyday prefab workflow (commit one
> tweak, discard another). Adds a new action-dispatch command.

## Command

New **`manage_prefab_overrides`** (category `asset`), in `AssetManagementHandler`. Every action requires a
`gameObjectPath` to a **prefab instance** in the scene (`GameObject.Find` → `PrefabUtility.IsPartOfPrefabInstance`,
else `NOT_FOUND`/`INVALID_STATE`).

- **`list`** (default, read-only) — `PrefabUtility.GetPropertyModifications` →
  `{ propertyModifications: [{ target, propertyPath, value, objectReference }], propertyModificationCount,
  truncated, addedComponents, removedComponents, addedGameObjects, prefabPath }`. `limit` caps the listed
  modifications (default 100) — the dependency/F2 paging convention.
- **`apply_property`** — `{ componentType, propertyPath }` → `SerializedObject(target).FindProperty(path)` →
  `PrefabUtility.ApplyPropertyOverride(prop, prefabPath, UserAction)`. `componentType` resolves a component by
  `Name`/`FullName` (or `GameObject` for the GO itself); missing → `NOT_FOUND`. Unknown property → `NOT_FOUND`.
- **`revert_property`** — same resolution → `PrefabUtility.RevertPropertyOverride(prop, UserAction)`.
- **`apply_all`** — `ApplyPrefabInstance` (counts via `GetObjectOverrides`).
- **`revert_all`** — `RevertPrefabInstance`.

Write actions (`apply_*`/`revert_*`) refuse in play mode (`PLAY_MODE`) — consistent with the F-section mutation
guards. `list` has no guard.

## Floor-safety

`PrefabUtility.GetPropertyModifications` / `ApplyPropertyOverride` / `RevertPropertyOverride` /
`ApplyPrefabInstance` / `RevertPrefabInstance` / `GetAddedComponents` / `GetRemovedComponents` /
`GetAddedGameObjects` / `GetObjectOverrides` / `GetPrefabAssetPathOfNearestInstanceRoot` are all floor-safe
(2018.3+/2019+). No `#if` guards; nothing for COMPATIBILITY.md.

## Catalog & testing

`manage_prefab_overrides` (category `asset`, `sides:["editor"]`), registered in `BuildDispatcher`. EditMode test:
create a prefab asset + instantiate it, override a transform property on the instance, `list` (modification
present) → `revert_property` (gone) → re-override → `apply_property` (source updated) → error paths (not an
instance, missing propertyPath, unknown component); clean up the temp asset + instance. Dogfood on 2020.3.

## Cadence

Branch toward batched **0.20.0** (slice 3). Remaining: dependency paging (slice 4), then docs + release.
