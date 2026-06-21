# Transform Space + Component Reorder / RequireComponent (0.17.0) Design

> Status: design (autonomous). Requirements **F3** (transform ops with explicit local/world space) and **F4**
> (component add/remove/reorder with RequireComponent awareness).

## 1. Gaps found

- **F3:** `modify_gameobject` sets `position`/`rotation` in **world** space (`transform.position`/`.rotation`) but
  `scale` in **local** (`localScale`) — an implicit, mixed convention with no way to set local position/rotation.
- **F4 reorder:** there is **no** component-reorder command. Component order matters (execution order /
  serialization), and legacy work often needs it.
- **F4 RequireComponent (a real bug):** `RemoveComponent` does `Undo.DestroyObjectImmediate(comp)`. Unity blocks
  removing a component another component `[RequireComponent]`s — it logs an error and **leaves the component**,
  but the handler still returns `removed:true`. A false success.

## 2. Changes

- **F3 — `space` on `modify_gameobject`:** `space` ∈ `{ "world" (default), "local" }`. `world` → `transform.position`
  / `transform.rotation = Quaternion.Euler(...)`; `local` → `transform.localPosition` / `transform.localEulerAngles`.
  `scale` is always `localScale` (no world scale) — documented. Same `space` added to `create_gameobject`.
- **F4 — `reorder_component` (new command):** move a component up/down among its siblings via
  `UnityEditorInternal.ComponentUtility.MoveComponentUp`/`MoveComponentDown` (registers Undo). Params:
  `gameObjectPath`, `componentType`, `componentIndex` (which instance, default 0), `direction` ∈ `{up,down}`,
  `count` (default 1). Refuses in play mode. Returns `{ componentType, direction, moved }` (moved = steps taken,
  clamped at the ends). Not confirm-gated (reversible + Undo).
- **F4 — RequireComponent-aware `remove_component`:** before destroying, scan the GameObject's *other* components
  for a `[RequireComponent]` whose required type is assignable-from the type being removed. If one exists, refuse
  with **`COMPONENT_REQUIRED`** naming the dependent — unless `force:true`. Fixes the false-success bug.

## 3. Floor-safety

`Transform.localPosition`/`localEulerAngles`, `ComponentUtility.MoveComponentUp`/`Down` (long-standing),
`RequireComponent.m_Type0/1/2` (public fields), `Type.GetCustomAttributes` — all floor-safe (2020.3). Nothing
for COMPATIBILITY.md.

## 4. Catalog & integration

`modify_gameobject` + `create_gameobject` gain `space`; `remove_component` gains `force`; new `reorder_component`
(category `component`). Registered in `BuildDispatcher`. Manifest regenerated, drift green.

## 5. Testing (EditMode)

- **F3:** `modify_gameobject space:"local"` sets `localPosition`/`localEulerAngles` on a *parented* object (so
  local ≠ world); `space:"world"` (default) sets world. Read back the right field.
- **F4 reorder:** add two components, `reorder_component direction:"up"` on the second → its sibling index drops
  (verify order via `GetComponents`); `moved` reflects steps; moving past the end clamps (`moved` < `count`).
- **F4 RequireComponent:** a component with `[RequireComponent(typeof(X))]` present → `remove_component` of X →
  `COMPONENT_REQUIRED`; with `force:true` → removed. (Use a test fixture component with a RequireComponent, or a
  built-in pair if one exists on the floor.)
- **Dogfood:** `modify_gameobject space:"local"` + `reorder_component` on the Main Camera-ish object over the bridge.
