# Serialization — `[SerializeReference]` Writing & `Gradient` (0.9.0) Design

> Status: design (autonomous). Closes the Serialization Core (requirements **D7** + `Gradient`), building on
> 0.7.0 (value read/write) and 0.8.0 (array mutation + curve write). After this, section D is complete.

## 1. Scope

**In 0.9.0:**
- **`[SerializeReference]` writing (D7):** set a managed reference to an instance of a chosen concrete type;
  clear it (null); write into the managed-reference subtree; read `managedReferenceFullTypename` (already in
  0.7) + expose the field constraint for discovery.
- **`Gradient` writing:** the structured read shape becomes writable (reflection on the floor).

Both flow through the existing `set_serialized_properties` — no new tool. After this section **D is done**
(D1–D12 + curve/gradient/managed-ref). Remaining roadmap moves to **E** (asset/prefab).

## 2. `[SerializeReference]` — the model

A `[SerializeReference]` field holds a polymorphic *managed* reference (a plain C# object, not a
`UnityEngine.Object`). The `SerializedProperty` exposes:
- `managedReferenceFullTypename` — the current concrete type (read; already emitted by 0.7's value model).
- `managedReferenceFieldTypename` — the **field's declared type** = the assignability constraint (2020.1+).
- `managedReferenceValue` — assign an instance (2019.3+).

### 2.1 Setting the type (through `set`)
The value for a `ManagedReference` property is:
- `{ "$type": "<type name>" }` — instantiate that concrete type (default) and assign it; **or**
- `null` — clear the reference.

`<type name>` resolves by: assembly-qualified name → `Type.GetType`; else a search of loaded assemblies by
full name then simple name (first assignable match). The resolved type is **validated assignable** to the
field's `managedReferenceFieldTypename` constraint; a non-assignable type → `TYPE_NOT_ASSIGNABLE` (no write).
Instantiation: `Activator.CreateInstance(type)` (runs the parameterless ctor for sensible defaults) →
fallback `FormatterServices.GetUninitializedObject(type)` when there is no parameterless ctor (Unity's own
deserialization path). Then `property.managedReferenceValue = instance`.

CAS: `expected` is the current `managedReferenceFullTypename` (a string, or `null`/empty when unset). Changing
the type is the structural change the size-of-0.8-CAS analog guards. `force` bypasses as everywhere.

### 2.2 Nested writes
Once a managed reference is set, its instance fields are navigable by `propertyPath`
(`_strategy.someField`), so the agent writes them with ordinary `set` calls — no special nested-write
protocol. `inspect` walks into a set managed reference (depth-limited) so the subtree is discoverable.

### 2.3 Discovery (introspection first — reuses 0.6)
`inspect` emits **`managedReferenceFieldTypename`** (the constraint) on a `ManagedReference` node. The agent
then lists the concrete, instantiable types assignable to that constraint with the **existing
`find_implementations`** (0.6 lite layer, `TypeCache.GetTypesDerivedFrom`). No new discovery tool — the agent
discovers, never guesses the `$type`.

## 3. `Gradient` — reflection-based on the floor

`SerializedProperty.gradientValue` is `internal` until Unity 2022.2, so 0.9.0 accesses it by **reflection**
(a single code path that works on the 2020.3 floor and newer; documented in COMPATIBILITY.md as a
reflection workaround, not an `#if` floor break). Structured value:
`{ "colorKeys": [ { "color": {r,g,b,a}, "time" } ], "alphaKeys": [ { "alpha", "time" } ], "mode": "Blend|Fixed" }`.
Read builds it from the `Gradient`; write parses it → `new Gradient { colorKeys, alphaKeys, mode }` →
reflection-set. If the reflected property is unavailable (a future Unity rename), read returns a `null` marker
and write returns `TYPE_MISMATCH` — never a crash.

## 4. Value-model changes (`SerializedValue`)

- `ManagedReference`: **read** stays `managedReferenceFullTypename` (string); **write** gains the
  `{$type}`/null case (instantiate + assign, with the assignability check delegated to a
  `ManagedReferenceResolver`). The Tree walk already treats it as a leaf — it now also emits
  `managedReferenceFieldTypename` alongside `managedReferenceFullTypename`, and recurses into a *set*
  reference's subtree (depth-limited).
- `Gradient`: moves from the read-only `default` to a real read (reflection) + write case.

## 5. Error model additions

Adds `TYPE_NOT_ASSIGNABLE` (the `$type` is not assignable to the managed-reference field constraint) and
`TYPE_NOT_FOUND` (the `$type` name did not resolve) to the existing set. `Gradient` reflection failure →
`TYPE_MISMATCH` with a clear message. Per-property `skipped[]` + CAS + `force`, exactly as 0.7/0.8.

## 6. Floor-safety & compatibility (2020.3 / C# 8 / netstandard 2.0)

- `managedReferenceValue` (2019.3+), `managedReferenceFullTypename` (2019.3+), `managedReferenceFieldTypename`
  (2020.1+) — all available on the 2020.3 floor. `Activator.CreateInstance` /
  `System.Runtime.Serialization.FormatterServices.GetUninitializedObject` — BCL, floor-safe.
- **`Gradient`:** reflection access to the internal `gradientValue` — added to COMPATIBILITY.md as a
  reflection workaround (the only one in the serialization core). No `#if` floor break.
- Document the 2020.3 `[SerializeReference]` quirks discovered during the dogfood (e.g. subtree propertyPath
  behavior, whether `GetUninitializedObject` instances serialize cleanly) in COMPATIBILITY.md / code comments.

## 7. Testing (NUnit EditMode)

- **Managed ref:** a fixture with a `[SerializeReference]` field of an interface/abstract base + ≥2 concrete
  types. Set by `$type` (assert the instance type lands); a nested field write into the set reference; clear
  to null; `TYPE_NOT_ASSIGNABLE` (a type not implementing the base); `TYPE_NOT_FOUND` (a bogus name); CAS on
  `managedReferenceFullTypename`. `inspect` exposes `managedReferenceFieldTypename` + recurses the subtree.
- **Gradient:** round-trip a gradient (colorKeys/alphaKeys/mode) read→write→read equal; a CAS write.
- **Floor:** dogfood on 2020.3 — set a managed reference + a gradient on a real object.
