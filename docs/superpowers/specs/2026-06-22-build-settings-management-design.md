# Build-Settings Scene Management (0.20.0, E-tail slice 1) Design

> Status: design (autonomous). Section **E** tail. There is no command to manage `EditorBuildSettings.scenes`
> (the build's scene list). `create_scene` appends to it, but you can't list / add / remove / reorder / toggle
> build scenes — a constant need on legacy projects (fixing a broken build list, reordering the boot scene).

## Command

New **`manage_build_settings`** (category `scene`), action-dispatch like the other `manage_*` handlers, added
to `SceneHandler` (it already manipulates `EditorBuildSettings.scenes`). Actions:

- **`list`** (default) — return `{ scenes: [{ path, enabled, index, exists }], count, enabledCount }`. Read-only.
- **`add`** — `scenePath` (required, must be an existing `.unity` within the project), optional `enabled`
  (default true) and `index` (default append). Refuses a duplicate path (`ALREADY_EXISTS`) and a non-existent /
  out-of-project path (`VALIDATION_ERROR` / `NOT_FOUND`).
- **`remove`** — by `scenePath` or `index`; `NOT_FOUND` if absent.
- **`move`** — `scenePath` or `index` (from) + `toIndex`; reorders (clamps `toIndex`).
- **`set_enabled`** — `scenePath` or `index` + `enabled` (bool).
- **`clear`** — empties the list.

The list is read via `EditorBuildSettings.scenes.ToList()`, mutated, and written back via
`EditorBuildSettings.scenes = arr` — the existing pattern. `exists` is `File.Exists(path)` so a broken build
list (dangling scene paths) is visible. Paths validated with `PathSafety` + a `.unity` check on `add`.

## Error model / floor-safety

`VALIDATION_ERROR`, `NOT_FOUND`, `ALREADY_EXISTS`, `INDEX_OUT_OF_RANGE`. `EditorBuildSettings.scenes` /
`EditorBuildSettingsScene(path, enabled)` are floor-safe (all versions). No play-mode guard — this is project
config, not scene-content state, and is safe to change in play mode. Nothing for COMPATIBILITY.md.

## Catalog & testing

`manage_build_settings` (category `scene`, `sides:["editor"]`), registered in `BuildDispatcher`. EditMode test:
round-trip add → list (present, enabled) → set_enabled false → move → remove → list (gone), restoring the
original `EditorBuildSettings.scenes` in TearDown. Dogfood on the 2020.3 floor.

## Cadence

Built on the feature branch toward a batched **0.20.0** release (not tagged per slice — tags now auto-publish to
npm + OpenUPM). Subsequent E-tail slices: import platform overrides, granular prefab-override apply, dependency paging.
