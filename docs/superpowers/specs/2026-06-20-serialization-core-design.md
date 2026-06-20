# Serialization Core — Safe Serialized-Property Editing (0.7.0) Design

> Status: design (brainstormed). Next: implementation plan (superpowers:writing-plans).
> Requirements: `.claude/unity-mcp-fork-requirements.md` section **D** (D1–D12) — "the fork's depth identity (the centerpiece)."

## 1. Context & mission

The base fork edits component members by **C# reflection**, which cannot reach private `[SerializeField]`
fields, mishandles Unity value types, and bypasses Inspector-correct undo/dirty/prefab semantics. The
Serialization Core replaces that with a pipeline built on **`SerializedObject`/`SerializedProperty`** —
Unity's own serialization, the same path the Inspector uses — targeting any `UnityEngine.Object`: scene
components, **ScriptableObject assets**, materials, asset importers.

This is the headline depth feature of the fork. 0.7.0 delivers a **complete, safe, testable spine**; the
hardest sub-problems (`[SerializeReference]` writing, array mutation, asset/prefab lifecycle) are deferred to
later versions (§11).

## 2. The two problems this design solves

1. **Tool-call economy.** A naive `set_property(target, path, value)` is one call per property per object.
   A realistic edit — 400 instances × 4 fields, plus the read — is ~2000 tool calls. Unworkable. The unit of
   work must be a **batch**, and many targets must be addressable by a **selector**, not one-by-one. With
   that, the same job is ~3 calls: read one representative's schema, write the batch by selector, save.
2. **Blind-write risk.** An agent must not overwrite a value it never looked at. We enforce
   **read-before-write**: the agent proves it knows the current state (compare-and-swap, or a previewed
   selector), or it explicitly opts into recklessness with `force`.

## 3. Design principles

- **Batch-first.** The write tool's unit is a *batch of edits*; single-property writes are just a batch of
  one. (Reclassifies requirements D10 "batch" from P1 to the spine.)
- **Friction is the feature.** The default path is the *safe* one and costs an extra step (read → echo the
  expected value). An agent drifts toward less friction, so the low-friction path is made the safe path, and
  blind writes sit behind one conscious, named door: `force`. "Overwrote something it never looked at" is
  structurally impossible unless the agent declared it didn't care.
- **Introspection first.** Agents *discover* a target's serialized tree (paths, types, values); they never
  guess `propertyPath`s.
- **Read/write symmetry.** The value a read emits is byte-for-byte what a write accepts — a read round-trips
  into a write (and into `expected`) with no translation.
- **Inspector-equivalent correctness.** Undo, dirty, prefab-instance modifications, and an explicit save
  policy match what the Inspector does — never a half-correct mutation.

## 4. Tool surface (three tools)

All are editor-side commands (`sides:["editor"]`), reached via `call_unity_tool`. Tool names are
`verb_noun` snake_case.

### 4.1 `inspect_serialized_object` — discovery (read, never mutates)
Returns the serialized property tree so the agent learns structure + current values.

- **Params:** a **single target** (`target`, §5.1) *or* a **selector** (`match`, §5.2); optional
  `component`/`componentIndex` (§5.1; omitted on a GameObject returns every component's tree); plus options:
  - `pathPrefix` (string, optional) — scope to a subtree (e.g. `"_states"`) instead of the whole object.
  - `depth` (int, default 3) — max nesting depth.
  - `includeValues` (bool, default true) — paths+types only when false (cheap schema scan).
  - `maxObjects` (int, default 50, ceiling 500) — cap on a selector's matched objects.
- **Result (single target):** `{ target: <resolved descriptor>, object: { type, path, properties: [ { propertyPath, propertyType, value, arraySize?, managedReferenceFullTypename? }, … ] } }`.
- **Result (selector):** `{ count, truncated, objects: [ { target, properties: […] }, … ] }`.
- Honors the `fields` projection meta-param to trim the response.

### 4.2 `set_serialized_properties` — the safe batch write
Two addressing modes (§6), both read-before-write by default, one `force` escape.

- **Params:**
  - `edits` (Mode 1, §6.1) — explicit-target compare-and-swap edits, **or**
  - `match` + `set` (+ `token`) (Mode 2, §6.2) — selector edit with preview-then-commit.
  - `force` (bool, default false) — opt into a blind write (skips `expected` / the `token`).
  - `dryRun` (bool, default false) — report planned changes + precondition results without mutating.
  - `allOrNothing` (bool, default false) — any precondition failure aborts the whole batch (else per-edit).
  - `withoutUndo` (bool, default false) — `ApplyModifiedPropertiesWithoutUndo` (no undo entry).
  - `undoLabel` (string, optional) — the Undo group name.
- **Result:** `{ applied, changed: [ { target, propertyPath, from, to } ], skipped: [ { target, propertyPath, code, expected?, actual? } ], token? }`. A selector *preview* (no token, no force) returns `applied:false`, the matched set with current values, and a `token`.

### 4.3 `save_assets` — explicit persistence
Writes are dirty-only; this persists. `{ targets?: [descriptors] }` (omitted = all dirty). Maps to
`AssetDatabase.SaveAssets()` / `SaveAssetIfDirty(...)`. Never called implicitly by a write.

## 5. Addressing

### 5.1 Single target (discriminated)
Resolves to exactly one `UnityEngine.Object` → one `SerializedObject`:
- `{ scenePath: "/Canvas/Button" }` — scene GameObject by hierarchy path.
- `{ instanceId: 12345 }` — any object by instance id.
- `{ guid: "abc…" }` / `{ assetPath: "Assets/Foo.asset" }` — an asset (ScriptableObject, material, …); a
  sub-asset is addressable by `{ assetPath, subAssetName }`.

**`component` / `componentIndex`** are **sibling fields** (NOT part of the discriminated target/match
object) — for a GameObject target or match, they select *which* component's `SerializedObject`
(`componentIndex` disambiguates multiples of the same type, default 0). Omitted for asset targets (the asset
*is* the object). In Mode 1 they sit inside each `edits[]` entry (different edits may target different
components); in Mode 2 they sit alongside `match`. On `inspect` they are optional — omitted for a GameObject,
the result returns *every* component's tree.

### 5.2 Selector (`match`) — the scaling lever (collapses N→1)
Matches many targets, scoped to the active/loaded scene(s) unless an asset selector:
- `{ prefab: guid|path }` — all instances of a prefab in the scene.
- `{ componentType: "Enemy" }` — all GameObjects carrying that component (the component is the object).
- `{ tag: "Respawn" }`, `{ selection: true }` (current editor selection), `{ scenePaths: ["/A","/B"] }`.
- Combined with `{ component, componentIndex }` to pick the SerializedObject per matched GameObject.
A selector is **homogeneous-intent**: the same `set` applies to every match.

### 5.3 Property addressing
Unity's own `propertyPath` grammar verbatim, including `Array.data[i]` and nesting
(`_states.Array.data[2]._gate`). `inspect` returns the exact paths — the agent never constructs them blind.

## 6. The safety model

### 6.1 Mode 1 — explicit targets → compare-and-swap (one call)
```
set_serialized_properties({ edits: [
  { target: {scenePath:"/Player"}, component:"Health",
    set: { "_current": { value: 100, expected: 80 },
           "regen":    { value: 2,   expected: 1 } } }
]})
```
- Each property writes **iff** the live value equals `expected` (value-equality per type, §7). Mismatch →
  the property is **skipped** with a `STALE` entry reporting `{expected, actual}`; under `allOrNothing` the
  whole batch aborts before any write.
- `expected` is **mandatory**. A `set` entry without `expected` is refused (`MISSING_PRECONDITION`) unless
  `force:true`. The agent already has the value from `inspect`, so supplying `expected` is free.
- No preview step — the agent named these targets deliberately ("set with ids without question," but never
  blind).

### 6.2 Mode 2 — selector → preview-then-commit via a token
```
// 1) preview (no token): does NOT mutate
set_serialized_properties({ match:{prefab:"Enemy.prefab"}, component:"Enemy", set:{ "speed": 9 } })
  → { applied:false, objects:[{target, current:{speed:…}}, …N], token }
// 2) commit: pass the token back
set_serialized_properties({ match:{prefab:"Enemy.prefab"}, component:"Enemy", set:{ "speed": 9 }, token })
  → { applied:true, changed:[…] }
```
- The **no-token** call is the preview: it resolves the match, returns each matched target's **current
  values for the touched paths**, and a `token`. This is "otherwise agents wouldn't know the differences" —
  now they do, before mutating.
- The **token** is *stateless*: a hash of `(sorted matched instanceIds, their current values at the touched
  paths)`. On commit the server re-resolves the match and recomputes the hash; equal → apply; different →
  `STALE_MATCH` (the matched set or a value changed since preview) → re-preview. No server-side session
  state. The token binds the *blast radius + prior state*, not the new values (the agent may adjust `set`).
- `force:true` skips the token requirement — apply the uniform `set` to every match regardless of prior
  state (the "I'm sure, set everything to N× regardless" path).

### 6.3 `force` — the one unsafe door
A single, explicitly-named flag that drops the read-before-write guarantee (skips `expected` in Mode 1 / the
`token` in Mode 2). Never a default; always surfaced in the result as `forced:true` per affected edit.

## 7. Value model & read/write symmetry

A canonical JSON encoding per `SerializedPropertyType`, **identical** for `inspect` output, `set` `value`,
and `expected`:
- Integer/Boolean/Float/String/Enum(`{name}` or `{index}` accepted; emitted as `name`)/Character.
- `LayerMask` (int mask), `Vector2/3/4` (`{x,y,…}`), `Rect`, `Bounds`, `Quaternion`
  (`{x,y,z,w}` + accepts `{euler:{x,y,z}}` on write), `Color` (`{r,g,b,a}`, HDR-safe), `Vector2Int/3Int`.
- `ObjectReference` — emitted + accepted as `{ guid, assetPath?, scenePath? }` (or `null`); resolved on
  write by GUID → asset path → scene hierarchy path; `null` clears.
- `ArraySize` (int, read-only here — mutation is D8/0.8); array *elements* are addressed by `propertyPath`.
- `[SerializeReference]` — `managedReferenceFullTypename` is **read** (introspection); writing is 0.8.
- `AnimationCurve`/`Gradient` — **read** as structured JSON; writing is 0.8.

Value-equality (for `expected`/`token`) compares the canonical form; `ObjectReference` compares by GUID /
instance identity; floats compare on the round-tripped canonical form (exact when the agent echoes `inspect`).

## 8. Property type matrix (0.7.0)

Read **and** write: all of §7 except those marked read-only (`ArraySize`, `[SerializeReference]`,
`AnimationCurve`, `Gradient` are read-only in 0.7.0). Writing an unsupported/mismatched type →
`TYPE_MISMATCH` with the expected `SerializedPropertyType`.

## 9. Correctness semantics (Inspector-equivalent, D9)

Per batch, in order:
1. `Undo.RegisterCompleteObjectUndo` (or `RecordObject`) on every touched object, under **one Undo group**
   (`Undo.SetCurrentGroupName(undoLabel)` + `Undo.CollapseUndoOperations`), unless `withoutUndo`.
2. Mutate via `SerializedProperty` setters on a fresh `SerializedObject(target)`.
3. `serializedObject.ApplyModifiedProperties()` (or `ApplyModifiedPropertiesWithoutUndo()` when `withoutUndo`).
4. For a **prefab instance** target: `PrefabUtility.RecordPrefabInstancePropertyModifications(target)` so the
   change registers as an override and the prefab link is preserved.
5. `EditorUtility.SetDirty(target)` (assets) — scene objects mark the scene dirty.
6. **No save.** Persistence is the separate `save_assets`.

**Play mode:** scene-object writes refuse in play mode with `PLAY_MODE` (ephemeral + dangerous); asset writes
are allowed. `force` does not override the play-mode guard.

## 10. Error model (structured, per edit)

`STALE` (Mode 1: actual ≠ expected, with both), `STALE_MATCH` (Mode 2: matched set/state changed since the
token), `MISSING_PRECONDITION` (no `expected`/`token` and no `force`), `TYPE_MISMATCH`, `TARGET_NOT_FOUND`,
`COMPONENT_NOT_FOUND`, `PROPERTY_NOT_FOUND`, `PLAY_MODE`, `VALIDATION_ERROR`. Each carries the offending
`target`/`propertyPath`. A partial batch reports `changed[]` + `skipped[]`; `allOrNothing` makes it atomic.

## 11. Scope

**In 0.7.0:** the three tools (§4); single + selector addressing (§5); the two-mode safe write (§6);
the read/write value model + symmetry (§7); the property matrix (§8, common types read/write, the four
read-only); Inspector-correct semantics incl. prefab-instance overrides (§9); the structured error model
(§10); **private `[SerializeField]` proven by an explicit test (D6) — the headline.**

**Deferred (0.8+):** array/list **mutation** (resize/insert/remove/move — D8, a distinct op shape);
`[SerializeReference]` **writing**/instantiation (D7, with 2020.3 quirks); `AnimationCurve`/`Gradient`
**writing**; demoting the reflection path to `set_member_reflection` (D12); all asset/prefab **lifecycle**
(section E — Create/duplicate/move/delete, prefab stage editing, variants, import settings).

## 12. Floor-safety & compatibility (2020.3, C# 8 / netstandard 2.0)

`SerializedObject`/`SerializedProperty`/`Undo`/`EditorUtility`/`PrefabUtility` are stable since 2019.
Guarded/cataloged points (COMPATIBILITY.md):
- `managedReferenceFullTypename` read — 2019.3+ (fine on the 2020.3 floor; guard only if we ever target 2019.2).
- `SerializedProperty.boxedValue` (2022.2+) is **not** used — 0.7.0 reads/writes via the per-type typed
  accessors (`intValue`, `vector3Value`, …) which exist on the floor.
- `Vector2IntValue`/`Vector3IntValue`, `Vector2Int`/`Vector3Int` property types — available 2017.2+.
- Any version-divergent accessor goes behind `#if UNITY_X_Y_OR_NEWER` with both branches maintained, and is
  added to COMPATIBILITY.md. Unity-side C# stays C# 8 / netstandard 2.0; IMGUI-safe; no UI Toolkit.

## 13. Protocol & catalog integration

`inspect_serialized_object`, `set_serialized_properties`, `save_assets` → `protocol/catalog/commands.json`
(`sides:["editor"]`, category `serialization`, full `params` + `result` schemas), a `SerializedMemberHandler`
in `unity-editor-mcp/Editor/Handlers/` returning `HandlerOutcome`, registered on the dispatcher in
`BuildDispatcher`, manifest regenerated, drift gate green. The `fields` projection applies to results. The
**1 MB framed-message cap** bounds a single batch payload: uniform selector edits are tiny; very large
*heterogeneous* explicit batches must be chunked client-side (documented; the handler rejects an oversized
frame at the transport layer as today).

## 14. Testing strategy

NUnit **EditMode** tests (the only place `SerializedObject` runs; the dotnet Core lane cannot cover this):
- **Headline:** write a **private `[SerializeField]`** field and read it back (the base-fix proof).
- CAS: `expected` match writes; `expected` mismatch → `STALE`, no write; missing `expected` → refused; `force`
  bypasses.
- Selector: preview returns matched set + current values + token; commit with token applies; stale token →
  `STALE_MATCH`; `force` skips the token.
- Each writable `SerializedPropertyType` round-trips (read → write → read equals); `TYPE_MISMATCH` on bad type.
- `ObjectReference` resolution by GUID / asset path / scene path; null assignment.
- Prefab instance: a write registers as an override and preserves the link.
- `withoutUndo` vs. one-undo-group; dirty set; `save_assets` persists; play-mode scene write refused.

## 15. Open questions / risks

- **Selector cost at scale.** A `componentType` selector over a huge scene scans all objects. Mitigated by
  `maxObjects` + truncation; revisit if it bites.
- **Token granularity.** Hashing every touched value across a large match has a cost; acceptable for the
  preview path (already returning those values). If batches get huge, hash a digest.
- **Heterogeneous selector edits** (different value per match) are intentionally *not* a selector feature —
  that's an explicit `edits[]` batch (Mode 1), bounded by the frame cap and chunked if needed.
```
