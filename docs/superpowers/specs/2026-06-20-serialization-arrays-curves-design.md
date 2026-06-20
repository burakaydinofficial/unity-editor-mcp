# Serialization — Array Mutation & Curve Writing (0.8.0) Design

> Status: design (autonomous). Builds on the 0.7.0 Serialization Core
> (`docs/superpowers/specs/2026-06-20-serialization-core-design.md`). Implements requirements D8 (array ops)
> + `AnimationCurve` write — the two clean, floor-safe deferrals from 0.7.0.

## 1. Context & scope

0.7.0 shipped the safe `SerializedObject` editor: discover a tree, read/write field *values* by id (CAS) or
selector (preview-token), private `[SerializeField]` included. It deferred three things; 0.8.0 takes the two
that are mechanical extensions of that spine:

**In 0.8.0:**
- **Array/list structural mutation (D8):** resize, insert-at, remove-at, move, clear — bounds-checked, with a
  size-based read-before-write guard mirroring 0.7's CAS.
- **`AnimationCurve` writing:** the read shape from 0.7 becomes writable through the existing `set` path.

**Deferred to 0.9.0** (the "hard / floor-quirk" version): `[SerializeReference]` writing (D7 — type
instantiation by name, assignable-type discovery, polymorphic nested writes) and `Gradient` writing
(`SerializedProperty.gradientValue` is internal until 2022.2 — needs a reflection workaround on the floor).

## 2. Array mutation — a new tool `modify_serialized_array`

Structural array ops are a different shape than value-set (indices shift, size changes), so they get their own
tool rather than muddying `set_serialized_properties`. It reuses 0.7's targeting + correctness machinery.

- **Params:**
  - `ops`: `[ { target, component?, componentIndex?, arrayPath, op, expectedSize, index?, count?, toIndex?, value? } ]` — a batch (one Undo group).
  - `force` (skip the size guard), `dryRun`, `allOrNothing`, `withoutUndo`, `undoLabel` — identical semantics to `set_serialized_properties`.
- **`op` ∈ { `resize`, `insert`, `remove`, `move`, `clear` }:**
  - `resize` — set `arraySize` to `count` (grows by duplicating the last/default element; shrinks by truncation).
  - `insert` — `InsertArrayElementAtIndex(index)`; if `value` is given and the element is a leaf type, set it.
  - `remove` — `DeleteArrayElementAtIndex(index)`; for an **object-reference array** a non-null element is deleted *twice* (Unity nulls on the first delete, removes on the second — the documented 2020.3 quirk).
  - `move` — `MoveArrayElement(index, toIndex)`.
  - `clear` — `ClearArray()`.
- **Result:** `{ applied, changed: [ { target, arrayPath, op, fromSize, toSize } ], skipped: [ { target, arrayPath, code, ... } ] }`.

## 3. Safety model — size compare-and-swap (mirrors 0.7 §6)

Read-before-write stays **mandatory**, adapted to structure: every op carries **`expectedSize`** — the array's
current element count as the agent last read it (from `inspect`'s `arraySize`). The op applies **iff** the live
`arraySize == expectedSize`; mismatch → **`STALE_SIZE`** (reports `actual`/`expected`), no mutation.

- `expectedSize` is **mandatory** — an op without it is refused (`MISSING_PRECONDITION`) unless `force: true`.
- This prevents "insert at 5 / remove at 5" landing on a different element because the array grew or shrank
  since the agent looked. `force` is the one reckless door, same as 0.7.
- **Bounds:** `index`/`toIndex` must be in range for the op (`insert` allows `index == size`); out of range →
  **`INDEX_OUT_OF_RANGE`** with the valid range.
- The property at `arrayPath` must be an array (`isArray`, not a `String`); else **`NOT_AN_ARRAY`**.
- All ops are validated **up front** (probe) so `allOrNothing` aborts before any mutation, and a `TYPE_MISMATCH`
  on an `insert` `value` is surfaced (the 0.7 audit lesson).

## 4. `AnimationCurve` writing

`AnimationCurve` moves from read-only to writable in `SerializedValue.Write`: parse the 0.7 read shape
`{ keys: [ { time, value, inTangent, outTangent } ] }` → `new AnimationCurve(Keyframe[])` →
`property.animationCurveValue`. No new tool — it flows through the existing `set_serialized_properties` (CAS
applies; `expected` is the current curve's key array). The Tree walk already emits the curve value (leaf), so a
read round-trips into a write. `Gradient` stays read-only (0.9).

## 5. Error model additions

Adds `STALE_SIZE`, `INDEX_OUT_OF_RANGE`, `NOT_AN_ARRAY` to the 0.7 set (`STALE`, `STALE_MATCH`,
`MISSING_PRECONDITION`, `TYPE_MISMATCH`, `TARGET_NOT_FOUND`, `COMPONENT_NOT_FOUND`, `PROPERTY_NOT_FOUND`,
`PLAY_MODE`, `VALIDATION_ERROR`). Per-op `skipped[]` + `allOrNothing` atomicity, exactly as 0.7.

## 6. Correctness & floor-safety

Reuses 0.7's machinery verbatim: one `Undo` group per batch (`IncrementCurrentGroup`/`SetCurrentGroupName`/
`Collapse`, `try/finally`), `ApplyModifiedProperties` (+`WithoutUndo`), `RecordPrefabInstancePropertyModifications`
on prefab instances, `EditorUtility.SetDirty`, dirty-only (persist via `save_assets`), and the **play-mode
guard** on scene objects (force does not override).

All APIs are floor-safe (2020.3 / C# 8 / netstandard 2.0): `SerializedProperty.InsertArrayElementAtIndex`,
`DeleteArrayElementAtIndex`, `MoveArrayElement`, `ClearArray`, `arraySize` (all 2017+); `animationCurveValue`
(2018+); `Keyframe` ctor. No version-divergent API; nothing new for COMPATIBILITY.md.

## 7. Catalog & integration

One new catalog entry `modify_serialized_array` (`sides:["editor"]`, category `serialization`,
`destructive:true`), a `ModifyArray` method in `SerializedMemberHandler` reusing `SerializedTargeting` +
`PlayModeBlocks` + the Undo/dirty helpers, registered on the dispatcher, manifest regenerated, drift green.
`AnimationCurve` writing needs no catalog change (it extends `set`).

## 8. Testing (NUnit EditMode)

- **Array ops:** each of resize/insert/remove/move/clear round-trips on an `int[]` fixture (assert the C#
  array after); `insert` with a `value`; `remove` on an **object-reference array** (the double-delete quirk);
  bounds (`INDEX_OUT_OF_RANGE`); `STALE_SIZE` (wrong `expectedSize`); `MISSING_PRECONDITION` (no size, no
  force); `NOT_AN_ARRAY`; `allOrNothing` abort; `dryRun` no-mutation; one Undo group reverts a batch.
- **AnimationCurve:** write a curve, read it back equal (round-trip); a CAS write with `expected`.
- **Floor:** dogfood on 2020.3 — mutate a real array (e.g., a component's array property) and an `AnimationCurve`.
