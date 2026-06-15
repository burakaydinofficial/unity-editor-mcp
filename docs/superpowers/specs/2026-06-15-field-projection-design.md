# Field projection (`fields` meta-param) — design

**Goal.** Let an MCP agent optionally select which result fields it wants back, GraphQL-style, to
cut response tokens. Absent → full payload (backward-compatible). This replaces the dropped
result-schema *enforcement* idea: instead of spending context advertising a passive result schema,
we give an *actionable* selector. Field-name *advertisement* (showing the shape upfront) is deferred
to 0.5.0, where it will be editor-sourced via the manifest — **not** catalog-coupled.

**Why now.** The dispatch-rail migration unified every command on `HandlerOutcome.Ok(payload)`, so
projection can be applied **once** at the Core dispatch layer and cover all 78 commands. Pre-migration
(76-case switch) this was impossible. This is a direct payoff of the migration.

**Catalog-independence (hard constraint).** The 0.5.0 direction removes Node's *runtime* dependency
on the static command list (Node learns from the editor manifest). So this feature adds **no**
Node→catalog dependency: it is free-form (the agent passes paths; the editor projects), needs no
result schema anywhere, and the projection lives editor-side.

## Wire contract
- Any command accepts an optional reserved `fields` param: `string[]` of dot-paths.
- Absent / empty / not-an-array → full result payload (unchanged behavior).
- Present → the **success** payload is projected to the union of the paths. **Errors are returned
  unprojected** (an agent selecting fields must still see the full error).
- `fields` is a protocol-level meta-param (every command honors it); it is **not** a per-command
  catalog param, so drift is unaffected.

## Path semantics (dot-path, array-transparent)
`project(token, paths)`:
- **Array** → map: project each element by the same `paths` (arrays are transparent; paths describe
  element/object structure).
- **Object** → group paths by head segment; for each `head`: if `head` is missing in the object, skip
  (lenient — free-form means agents may guess); if any path terminates at `head` (leaf selection),
  include the whole subtree; otherwise recurse with the remaining tails.
- **Scalar** → returned as-is (so a path that descends *through* a scalar, e.g. `a.b` where `a` is a
  string, yields the whole scalar at `a`).
- Missing paths are silently omitted (no error).
- Segments are **case-sensitive** (matched verbatim against JSON keys).
- `fields` that is absent, not an array, empty, or an array with **no string elements** → full payload
  (the non-string tokens are skipped; if none remain, no projection happens).

Example: `fields = ["count", "objects.name", "objects.transform.position"]` on
`{count, objects:[{name, tag, transform:{position, rotation}}, …]}` →
`{count, objects:[{name, transform:{position}}, …]}`.

## Implementation
1. **`unity-editor-mcp/Core/FieldProjection.cs`** (new, Unity-independent): `static JToken Project(JToken payload, IReadOnlyList<string> paths)`. Pure; covered by `dotnet test` (objects, arrays, nesting, leaf-vs-deep on same head, missing paths, empty selection).
2. **`CommandDispatcher.Dispatch`** (Core): read `fields` from `request.Params` (only if a string array); pass params **without** `fields` to the handler (handlers never see the meta-param); on a **success** outcome with `fields` present, convert the payload to `JToken` (guarded — a non-serializable payload skips projection) and `Ok(FieldProjection.Project(token, fields))`. Errors pass through untouched. Covered by `dotnet test` against the dispatcher.
3. **Node `CallUnityToolToolHandler`**: treat `fields` as a reserved meta-param — strip it from `callParams` before validating against `entry.params` (future-proofs against a command that sets `additionalProperties:false`), then send the full params (incl. `fields`) through. Document `fields` in the tool description.
4. **Protocol wire spec**: document the `fields` meta-param.

## Out of scope (→ 0.5.0)
- Advertising available result field names (editor-sourced, via the enriched manifest).
- **Per-typed-tool schema advertisement of `fields`.** The generic surface (`call_unity_tool`, the
  default) documents `fields` in its params schema. On the opt-in typed surface
  (`UNITY_MCP_TYPED_TOOLS=true`) `fields` is still honored (it passes through to the editor) but is
  not advertised in each tool's inputSchema — that rides the 0.5.0 field-advertisement work (the
  typed surface is regenerated from the manifest there anyway). The 4 server-side instance meta-tools
  never project (no editor roundtrip).
- Any result-schema enforcement/validation.

## Verification
- `dotnet test` for FieldProjection + the dispatcher projection path.
- Floor dogfood: call a command with `fields` (e.g. `get_hierarchy` with `["sceneName","hierarchy.name"]`) and confirm the payload is trimmed; call without `fields` and confirm full payload.
- `test:ci`, drift (unchanged surface), compat-lint.
