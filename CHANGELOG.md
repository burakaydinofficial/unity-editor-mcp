# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims to follow semantic
versioning. This fork is **the deep, floor-true MCP bridge for older Unity projects** (Unity 2019.4 floor →
latest; CI-verified on 2019.4 / 2020.3 / 2021.3 / 2022.3 LTS). The npm server `@burakaydinofficial/unity-editor-mcp` and the UPM
package `com.burakk.unity-editor-mcp` ship together at the same version.

## [0.20.5] — Dogfood rounds 6–8, code review, + a comprehensive aftermath test suite

Three more live dogfood passes, an independent code review, and a rigorous "aftermath" verification sweep — then a
deterministic outcome-test suite covering the whole testable-in-EditMode tool surface. All CI-verified across
2019.4 / 2020.3 / 2021.3 / 2022.3 (295 EditMode tests).

### New / improved behavior

- **`set_active_scene`** — switch the active scene among loaded scenes (multi-scene companion to `load_scene` /
  `close_scene`).
- **`create_gameobject` `components`** — create a GameObject with components in one call (all-or-nothing: an unknown
  type is rejected and the object is not created). Now declared in the catalog, so it is discoverable.
- **Multi-scene awareness** — `get_hierarchy` and `analyze_scene_contents` span all loaded scenes and report a
  `loadedScenes` summary; each top-level root is tagged with its `scene`.
- **Inactive GameObjects are reachable by path** — the by-path resolvers (`modify_gameobject` /
  `get_gameobject_details` / `delete_gameobject` / …) now resolve inactive objects too, so deactivating an object no
  longer strands it (you can reactivate or delete it by path).
- **Serialization tools see the open prefab stage** — `inspect_serialized_object` / `set_serialized_properties` /
  `modify_serialized_array` (via `scenePath` / `match`) reach stage objects while a prefab is open.

### Fixed

- **`play_game`** refuses in `-batchmode` (entering play mode there freezes the bridge unrecoverably) instead of
  bricking the connection.
- **`create_gameobject`** rejects an empty name (it had created an unaddressable object at path `/`).
- **`delete_gameobject`** returns NOT_FOUND when nothing matches (was a 0-count "success").
- **`manage_tools`** reports the real installed package versions (dropped a fabricated cache).
- **`set_active_scene`** reports a failed switch as an error, verifying the effect — Unity's `SetActiveScene` bool
  return is unreliable (false even when the scene did change).
- **`set_serialized_properties` / `modify_serialized_array`** — a `componentType` match edits that component;
  `force:true` bypasses the compare-and-swap precondition; the audit log records a useful target for these.

### Quality

- A **deterministic aftermath EditMode test suite** (`Tests/Editor/Aftermath/`) — each test re-reads real Unity state
  through an independent path and asserts the actual effect, the defense against the false-success class. It covers the
  entire outcome-testable-in-EditMode tool surface; the genuinely-untestable tools (play-mode / recompile /
  test-runner / quit) are documented and were exercised live across the dogfood rounds.
- An **independent code review** (no Critical/High findings) and a rigorous **aftermath sweep** (34 mutating tools
  driven read→act→independent-re-read→assert, **zero false-successes**) hardened the bridge; the protocol catalog now
  declares the new params/result fields so they are discoverable.

## [0.20.4] — Prefab-stage same-name shadowing fix

Corrects a bug in the 0.20.3 prefab-stage work, found by code review: `FindGameObjectStageAware` resolved the main
scene *before* the open prefab stage, so a same-named main-scene object would shadow the stage object — a by-path
mutation (`add_component`/`modify_gameobject`/`delete_gameobject`/…) could silently hit the wrong target while
editing a prefab. It now resolves the open stage **first**, falling back to the main scene only when the path isn't
in the stage. Guarded by a `[UnityTest]`. (0.20.3 reached OpenUPM only — npm skipped it — so 0.20.4 brings both
registries back in lockstep on the corrected code.)

## [0.20.3] — Prefab-stage editing + round-5 dogfood

Two more live dogfood passes turned prefab-stage editing into a complete, consistent capability and fixed the
error-detail path. All CI-verified across 2019.4 / 2020.3 / 2021.3 / 2022.3.

### Prefab-stage editing (now end-to-end)

- **`get_hierarchy`** sees the open prefab stage's contents (not just the main scene).
- **`create_gameobject`** creates *into* the open stage — `parentPath` resolves stage objects, and a no-parent
  create lands under the prefab root so it joins the prefab.
- **All by-path resolvers are now stage-aware** — `add_component`, `list_components`, `get_gameobject_details`,
  `modify_gameobject`, `remove_component`, `reorder_component`, `get_component_values`, `delete_gameobject` resolve
  stage paths (they returned NOT_FOUND before). The open→create→add-components→modify→save flow works via the bridge.
- **`save_prefab` / `exit_prefab_mode`** save on explicit request with accurate messages (the prefab stage
  auto-saves, which had made them report "No changes to save"). Guarded by a `[UnityTest]`.

### Other fixes

- **Error responses** now carry the handler's structured `details` + `remediation` to the client (e.g. a confirm
  gate's dependents list) — they were dropped, leaving only message + code.
- **`reorder_component`** reports an honest `moved:0` message; documented the 2020.3-batch `ComponentUtility` no-op
  (it works on 2021.3+).
- **`create_scene`** rejects a `.unity` file path with a clear message (the path is the target folder).
- **`get_gameobject_details`** accepts a path-only call (no longer wrongly requires `gameObjectName`).
- **`manage_tags` / `manage_layers` / `manage_windows` / `manage_tools`** descriptions say `get` (the actual action), not `list`.

## [0.20.2] — Dogfood bugfixes (round 4)

A fourth live dogfood pass (a fresh agent driving the 2020.3 bridge) plus fixes for the silent-wrong-answer /
ignored-input issues it surfaced, each with a regression test:

- **`modify_gameobject`** — an undefined tag now fails (`VALIDATION_ERROR`) instead of false-succeeding while the
  object stayed Untagged.
- **`get_component_types`** — the name filter now accepts `searchTerm`/`query` aliases (not only `search`).
- **`enhanced_read_logs`** — honors the singular `logType`; statistics are scoped to the filtered set (were a
  whole-buffer tally).
- **`modify_prefab`** — a `name` modification is rejected with guidance (a prefab root name follows the .prefab
  file name; use `move_asset`) rather than reporting a no-op rename as success.
- **`modify_material`** — accepts a color/vector as an object (`{r,g,b,a}` / `{x,y,z,w}`), not only an array.
- **`read_logs` / `clear_logs`** — now read/clear the real editor console (`LogEntries`), like
  `enhanced_read_logs` / `clear_console`, instead of a capture buffer that reset on every domain reload.
- **`list_unity_tools`** — the bulk listing now includes each tool's `paramNames`, so agents need not guess
  parameter names.

Verified on the floor-matrix (2019.4 / 2020.3 / 2021.3 / 2022.3) and the Node test suite.

## [0.20.1] — Dogfood bugfixes

Fixes from three live dogfood passes — fresh agents drove the bridge end-to-end and reported friction, with
regression tests for each. Editor surface **99 commands** (close_scene added). **Floor lowered to Unity 2019.4**,
now CI-verified — the floor-matrix cold-compiles + runs EditMode on 2019.4 / 2020.3 / 2021.3 / 2022.3.

### Added

- **`close_scene`** — selectively unload one open scene (by scenePath / sceneName). Previously the only way to drop
  an additively-loaded scene was `load_scene` Single (which closes ALL scenes + reloads from disk). Refuses to
  close the last loaded scene; a dirty scene needs `save:true` (save then close) or `force:true` (discard).

### Fixed

- **`get_component_values` crashed on built-in components** — it dumped every public property and the response
  serializer then hit cyclic Unity graphs (e.g. `Matrix4x4.rotation.eulerAngles.normalized`), returning a
  "Self referencing loop" error string for Transform/Rigidbody/Light/etc. `SerializeValue` is now loop-proof
  (structured for known types, a reference-summary for Unity objects, `ToString()` otherwise).
- **`manage_asset_database` `find_assets` had no result cap** — an unscoped query could dump tens of thousands of
  lines. Added `limit` (default 100) plus `total` / `truncated` in the response.
- **`modify_gameobject` local space** — the result now reports `localPosition` / `localRotation` (not only
  world), and a reparent + `space:"local"` in one call now stores the local value relative to the NEW parent (it
  was computed against the old parent, since the reparent preserves world position).
- **`manage_prefab_overrides revert_property` collateral revert** — `instantiate_prefab` set name/position on the
  instance without recording them as overrides, so reverting an unrelated property discarded them (the instance
  snapped back to the prefab). It now records the transform/name overrides, so a single-property revert is
  genuinely granular.
- **`capture_screenshot captureMode:"game"`** returned a `NullReferenceException` when no rendering Game View was
  available; it now returns a clear `INVALID_STATE` that points to the `camera` / `scene` modes.
- **`find_implementations` had no result cap** — a common base type (ScriptableObject → 772 subtypes) overflowed
  the response channel. Added `limit` (default 200) + `total` / `truncated`.
- **`simulate_ui_input` simple mode was broken** — the schema advertised `elementPath`+`inputType`+`inputData` but
  the handler required `inputSequence`. Simple mode now works (translated to a one-action sequence); the
  `inputType` enum is trimmed to the supported `click` / `type`.
- **`inspect_serialized_object` `pathPrefix` leaked sibling properties** — it now strictly scopes to the subtree.
- **`delete_gameobject` `confirm`** is now documented in its schema (it was required but undocumented).
- **`get_object_references` missed component object-references** — it reflected over C# fields, so native serialized
  refs (e.g. `HingeJoint.connectedBody` / `m_ConnectedBody`) went undetected and an object looked unreferenced. It
  now scans the component's `SerializedObject` ObjectReference properties.
- **`clear_console` `preserveWarnings`/`preserveErrors` were a silent no-op** — they cleared everything (incl.
  errors) anyway. Unity has no native selective clear, so the flags are removed and the command now refuses them
  rather than destroying data.
- **`enhanced_read_logs` `groupBy:"type"` bucketed everything under "unknown"** — the grouper only handled the
  detailed (Dictionary) entry shape, not the compact (anonymous-object) one; it now normalizes via JObject.
- **`inspect_serialized_object` `pathPrefix` on a leaf returned `[]`** — a follow-up to the subtree-scope fix; a
  leaf prefix (e.g. `m_Name`, `m_Intensity`) now emits that property itself.

## [0.20.0] — Section E tail + static-method invoke (G6)

The section-E (asset/scene) tail plus static-method invoke — five capability slices, each verified on the 2020.3
floor (EditMode green) and dogfooded on the live bridge. Editor surface **98 commands across 18 categories** (was
95). All new APIs are floor-safe — no new `COMPATIBILITY.md` guards.

- **`manage_build_settings`** (new, scene) — manage the build scene list (`EditorBuildSettings.scenes`):
  `list` / `add` / `remove` / `move` / `set_enabled` / `clear`, with `exists` flagging dangling build paths.
  No prior command managed the build scene list.
- **`manage_prefab_overrides`** (new, asset) — inspect and granularly apply/revert prefab-instance overrides
  (`list`, `apply_property` / `revert_property`, `apply_all` / `revert_all`) — vs `save_prefab`'s all-or-nothing.
  Write actions refuse in play mode.
- **`manage_asset_import_settings`** — added per-platform texture overrides (`get_platform` / `set_platform`):
  per-platform `maxTextureSize`, `format`, `textureCompression`, `compressionQuality` (e.g. Android → ETC2/ASTC),
  with `iOS`→`iPhone` / `Windows`/`OSX`→`Standalone` aliases.
- **`analyze_asset_dependencies`** — `get_dependencies` / `get_dependents` now page via `limit` / `offset` and
  return `total` / `hasMore` alongside the page; also fixes a latent O(n²) direct-dependency recomputation.
- **`invoke_static_method`** (new, menu) — **G6**: invoke a static method by type + name with JSON args. Arbitrary
  code execution, so it ships behind **H2 default-deny** (`InvokePolicy`): denied (`INVOKE_DENIED`) unless
  `FullType.Method` matches an allow pattern from the `UNITY_MCP_INVOKE_ALLOW` env var or
  `ProjectSettings/UnityEditorMcpInvokePolicy.json`. Patterns: exact, `Ns.Type.*`, or `*`.

### Docs & DX

- Generated **per-tool reference** (`docs/tools-reference.md`) from the contract (J1); the README **support table**
  is now generated from the floor-matrix CI (A2); added a **troubleshooting matrix** (`docs/troubleshooting.md`, J3).
- `CONTRIBUTING.md` gains the **compatibility policy** + the gate suite, plus a **PR template** with the compat
  checklist (A8); the README adds a **Claude Code** quickstart (J2).

### Fixed

- **Core (C#) CI flake** — `TcpTransportTests` now shares one framer across reads, so coalesced socket reads
  (common in CI, rare on a fast local loopback) no longer drop the second framed reply. The production
  `TcpTransport` was already correct (drains all frames per read, serialises writes).
- **Code-review hardening (E-tail)** — `manage_build_settings` canonicalizes scene paths to project-relative (an
  absolute in-project path no longer bypasses the duplicate check); `get_dependencies` includes unloadable deps so
  a page can't return `count<limit` while `hasMore`; `analyze_asset_dependencies` now path-guards its `assetPath`.

## [0.19.0] — Deployment prep

First version prepared for publication. **npm + UPM aligned to 0.19.0** (previously stranded at 0.3.0 while the
feature work ran on local tags v0.4–v0.18). Docs refreshed to the real surface — README command catalog
regenerated from the contract (**95 commands across 18 categories**, was "~76/13"), Key Features rewritten, this
CHANGELOG extended, `floor-testing.md` corrected (Unity deprecated Personal manual activation). Floor-CI wired: a
GameCI EditMode matrix over **2020.3.49f1 / 2021.3.45f2 / 2022.3.62f2** with committed host projects under
`ci/unity-host-<version>/`.

## [0.18.0] — F1: inherited-handler contract sweep

A multi-agent sweep of the inherited handlers fixing 12 confirmed contract gaps: play-mode guards on the
component mutators (`add`/`modify`/`remove_component`); false-success fixes (`execute_menu_item`,
`modify_material`, `modify_prefab` no longer report success when the operation did not happen); missing-Undo
gaps (material edits, UI state); `play_game`/`stop_game` messaging made async-honest.

## [0.17.0] — Transform space + component reorder (F3/F4)

`modify_gameobject` gains a world/local `space`; new `reorder_component`; `remove_component` is now
`RequireComponent`-aware (`COMPONENT_REQUIRED` instead of a silent false success — a real bug fix).

## [0.16.0] — Missing-script detection (F5)

`find_missing_scripts` + `remove_missing_scripts` (active-scene, Undo + confirm-gated) — the legacy-project staple.

## [0.15.0] — Visual capture (G5)

The Node server emits real MCP **image content** for captures (the agent can see them), and `capture_screenshot`
gains a **specific-camera** render mode.

## [0.14.0] — Query paging / limits (F2)

`limit`/`maxNodes` caps with a `truncated` signal on `find_gameobject` / `find_by_component` / `get_hierarchy`,
keeping a large scene under the 1 MB frame cap.

## [0.13.0] — Path sandbox (Security H4)

Project-root containment sweep — 14 path-escape gaps closed; `Core.PathContainment` is dotnet-tested (the symlink
limitation is documented).

## [0.12.0] — Mutation audit log (Security H5)

`Library/UnityEditorMCP/audit-log.jsonl` (dispatcher-hooked, size-capped, fail-safe) with `get_audit_log` /
`clear_audit_log`.

## [0.11.0] — Confirm-gate (Security H3)

A central confirm-gate for irreversible commands (`delete_gameobject`, `delete_script`, `update_script`,
`quit_editor`, `set_project_setting`, `manage_packages`) — `CONFIRMATION_REQUIRED` until `confirm:true`.

## [0.10.0] — Asset & prefab lifecycle (section E)

`create_scriptable_object`, `unpack_prefab`, `create_prefab_variant`; a destructive-delete gate (dependents) and
scene-mutation play-mode guards.

## [0.9.0] — Serialization: `[SerializeReference]` + `Gradient`

Writes managed references and gradients — closes the Serialization Core (section D).

## [0.8.0] — Serialization: array/list mutation

`modify_serialized_array` (resize/insert/remove/move/clear with a size compare-and-swap) and `AnimationCurve` writing.

## [0.7.0] — Serialization Core

`inspect_serialized_object` / `set_serialized_properties` — a safe `SerializedObject` editor reaching private
`[SerializeField]` data, Inspector-correct (one Undo group; compare-and-swap / preview-token / force).

## [0.6.0] — Semantic code intelligence

An always-on lite reflection/`TypeCache` layer plus an opt-in, capability-gated **Roslyn** sidecar
(`resolve_symbol`, `get_type_members`, `find_implementations`, semantic `find_references`).

## [0.5.0] — Unreleased

Theme: **the lean Adaptive-Node client** ([ADR 0006](docs/adr/0006-no-default-instance-on-demand-discovery.md)).
The Node server stops hand-mirroring the editor's command surface: it advertises three generic
meta-tools and learns each connected editor's real tools — names, param schemas, and result-field
hints — from the handshake manifest at runtime. ~76 fewer Node files; one server drives any Unity
version and several editors at once.

### Changed (BREAKING — pre-1.0)
- **No default/active instance.** `call_unity_tool` and `list_unity_tools` now **require** an explicit
  `instance` (a project path or port), even when a single editor is running; a missing or unresolved
  instance is a hard, clearly-worded error, never a silent default. This closes a real safety hole: an
  agent revived after context compaction can no longer issue a destructive call against the wrong live
  project.
- **The MCP surface is three meta-tools** — `list_unity_instances`, `list_unity_tools`,
  `call_unity_tool`. Every editor command is reached via `call_unity_tool` after on-demand discovery
  with `list_unity_tools`; the 73 hand-written passthrough handlers were deleted. Three tools that carry
  genuine Node-side logic (`execute_menu_item`, `create_script`, `analyze_screenshot`) are dispatched
  inside `call_unity_tool` rather than advertised.

### Added
- **Editor-advertised result-field hints.** The handshake manifest now carries each command's result
  schema alongside its params; `list_unity_tools(instance, name: "<tool>")` returns it, so an agent
  learns a tool's response shape on demand and drives `fields` projection without a discovery
  round-trip. Editor-sourced — no Node→catalog dependency.

### Removed
- `set_active_unity_instance` (there is no default instance to set) and the `UNITY_MCP_TYPED_TOOLS`
  env var / typed-tool advertisement (the surface is always the three meta-tools).

### Internal
- The drift gate is re-pointed: with the JS passthroughs gone, only the three meta-tools are
  server-side in the catalog; the 76 editor commands are validated editor-side. The catalog↔JS
  param-drift class is eliminated.

## [0.4.0] — Unreleased

Theme: **master the existing structure.** Hardens the editor side onto a single tested dispatch
rail and adds editor/project + code-intelligence tools, all on the Unity 2020.3 floor.

### Added
- **Editor & project operations** (6 tools): `get_editor_info`, `get_project_settings`,
  `list_packages` (reads), `set_project_setting` (curated keys), `manage_packages` (UPM add/remove),
  `quit_editor`. All floor-safe (Unity 2020.3+) and synchronous.
- **Syntactic code intelligence** (4 tools): `get_symbols`, `find_symbol`, `find_references`,
  `get_symbol_body`. An in-editor, dependency-free C# analyzer (masks comments/strings, then regex +
  brace matching) — no Roslyn, no external LSP. (Full semantic analysis is a later milestone.)
- **GraphQL-style result field projection.** Any tool call accepts an optional reserved `fields`
  param — an array of dot-paths (e.g. `["count","objects.name"]`) — to trim the response to just
  those fields and cut tokens. Arrays are transparent (the path applies to each element); omit
  `fields` for the full result. Implemented once at the dispatcher, so it covers every command.

### Changed
- **Dispatch-rail migration (internal).** All editor commands now run on the Unity-independent,
  `dotnet`-tested `CommandDispatcher` via the `HandlerOutcome`/`CommandResult` contract — which
  cannot serialize an error as a success. The legacy `ProcessCommand` switch has been fully retired.
  Wire shapes are unchanged.
- **Precise error codes.** Handler errors now carry specific codes — `VALIDATION_ERROR`, `NOT_FOUND`,
  `INVALID_STATE`, `INTERNAL_ERROR` — instead of the previous generic `EDITOR_ERROR`.

### Notes
- Compatibility: all version-divergent Unity APIs stay behind `#if UNITY_*` guards (both branches
  maintained); guarded sites are cataloged in `COMPATIBILITY.md`.
- Out of scope (planned for later): a floor-CI matrix and release automation (0.4.x ship-prep);
  editor-advertised result-field discovery and the lean Adaptive-Node client (0.5.0); semantic
  (Roslyn) code intelligence (0.6.0).

## [0.3.0] — 2026-06-15

The protocol-contract + architecture foundation of the fork.

### Added
- **Version-agnostic generic surface** (ADR 0004): four meta-tools — `list_unity_instances`,
  `list_unity_tools`, `call_unity_tool`, `set_active_unity_instance`. The editor advertises its
  command manifest on the handshake and the Node server learns the tool surface at runtime, so one
  server drives any supported editor. (`UNITY_MCP_TYPED_TOOLS=true` re-advertises the full typed
  catalog as individual MCP tools.)
- **Multi-instance discovery + routing** (ADR 0003, 0005): per-project derived ports and a discovery
  registry, so multiple editors can be targeted by project path or port.
- **Protocol contract sub-project** (`protocol/`): the canonical command catalog, the wire/result/
  error spec, a protocol version line, and a dependency-free drift gate.

### Changed
- **Three-part architecture** (ADR 0001) and a Unity-dependency assembly split (ADR 0002): a
  Unity-independent `Core` (framing, dispatch, the result/error contract) that runs under `dotnet
  test` with no editor, and an `Editor` assembly holding the bootstrap, handlers, and all `#if
  UNITY_*` guards.
- **Domain errors are no longer laundered as success.** `ResponseClassifier` (Unity-independent,
  `dotnet`-tested) classifies a handler's `{ error: … }` return into a real error envelope.

### Compatibility
- Unity 2019 → latest, initial focus 2020.3–2022.3 LTS. Unity C# stays within C# 8 / netstandard 2.0;
  the Node server is pure ESM with no native modules.
