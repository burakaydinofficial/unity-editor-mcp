# Unity MCP Bridge Fork — Requirements Specification

> Fork base: `ozankasikci/unity-editor-mcp` @ `v1.3.1` (Unity package 0.14.0 + npm server 1.3.1 — the
> verified-on-2020.3 snapshot; 4-line PrefabStage namespace patch already proven). Mission: **the deep and
> floor-true bridge for older Unity projects** — while the ecosystem races broad and floor-raising.
> Every requirement is written to be independently implementable and verifiable. Priorities:
> **P0** = the fork's identity (ship nothing without these) · **P1** = first releases · **P2** = stretch.

---

## A. Identity & Governance — "well-maintained" as a structural property

- **A1 (P0).** Rename the Unity package off the reserved namespace: `com.unity.editor-mcp` → own scope
  (e.g. `com.<org>.unity-bridge`); npm under a matching scope (`@<org>/unity-bridge`). Preserve upstream MIT
  license + attribution. Document the migration from both upstream names.
- **A2 (P0).** **One source of truth for support claims.** The README support table is GENERATED from CI
  matrix results; a CI check FAILS the build if the package.json `unity` field, the README table, and the
  matrix config disagree. (The exact failure that killed the predecessors: README said 2020.3 for a year
  while the manifest said 6000.0.)
- **A3 (P0).** **CI version matrix as the product:** GitHub Actions (game-ci/unity-builder or equivalent)
  compiling the Unity package + running an EditMode smoke on **2020.3 LTS, 2021.3 LTS, 2022.3 LTS, 6000.x**
  on every PR; releases blocked on full green. Add editor versions only with their matrix lane.
- **A4 (P0).** Lockstep versioning: one release tags Unity package + npm server with the same semver; the
  server validates the version pair at handshake and reports a mismatch as a structured error (not a
  protocol hang).
- **A5 (P1).** Automated publishing from tags: OpenUPM + npm; keep-a-changelog discipline; release notes
  list per-editor-version compatibility deltas.
- **A6 (P0).** **No telemetry.** If ever added: opt-in only, documented, off by default.
- **A7 (P1).** Stated support policy: floor = 2020.3 LTS, with a "tested-on" table (matrix versions) and a
  policy line for when floors may ever move (major version only, with a maintained legacy branch).
- **A8 (P1).** CONTRIBUTING.md encodes the compatibility policy (section B); PR template includes the
  "matrix green / no unguarded new-API" checklist so contributions cannot silently raise the floor.

## B. Compatibility Engineering — floor-true by construction

- **B1 (P0).** **Guards, not floors:** every version-divergent Unity API sits behind
  `#if UNITY_X_Y_OR_NEWER` with BOTH branches maintained. A `COMPATIBILITY.md` catalog lists every guarded
  site (file:line, API, versions) so audits are greppable.
- **B2 (P0).** First known guard: `PrefabStageUtility`/`PrefabStage` —
  `UnityEditor.Experimental.SceneManagement` pre-2021.2, `UnityEditor.SceneManagement` after. (Patch
  already written; convert the hard-replace into the guarded form.)
- **B3 (P0).** **Runtime capability negotiation:** the handshake returns the editor version + a per-tool
  availability map. A tool unavailable on the connected editor version is reported as
  `unsupported_on_editor_version` by the server — never a runtime throw from missing APIs, never a silent
  no-op.
- **B4 (P0).** Unity-side C# constrained to C# 8 / netstandard 2.0 (the 2020.3 Mono reality). No UI Toolkit
  editor APIs in core paths (IMGUI-safe), or guarded per B1.
- **B5 (P1).** Node server: pure JS (no native modules — npx friction-free), tested on Node 18/20/22/24 LTS
  in CI; declared range enforced via `engines` + a startup check with a clear message.
- **B6 (P1).** A "floor smoke" test assembly inside the Unity package: a tiny EditMode test per handler
  category that exercises one real call path — this is what the CI matrix runs per editor version.

## C. Transport & Reliability — the domain-reload problem is THE problem

- **C1 (P0).** **Domain-reload survivability:** the editor-side listener re-arms via `[InitializeOnLoad]`;
  state that must span reloads uses `SessionState`/files; the Node server auto-reconnects with backoff and
  surfaces a `reconnecting` state to the MCP client rather than failing tools.
- **C2 (P0).** **In-flight command contract across reloads:** every command has an id; a command whose
  execution is interrupted by a reload yields a structured `interrupted_by_reload` result (never a hang);
  reload-CAUSING commands (refresh/script-write) define their outcome channel explicitly (see C5).
- **C3 (P0).** **Project-targeted connections:** configurable port (env + a ProjectSettings asset), default
  derived from the project path hash to avoid the fixed-6400 wrong-editor trap; the handshake reports
  project path + Unity version; the server refuses a project-path mismatch with a clear error. Multiple
  editor instances on one machine must be independently addressable.
- **C4 (P0).** Status tool (and heartbeat): alive, editor version, project path, `isCompiling`,
  `isPlaying`, pending compile errors count, test-run-active, last-reload timestamp.
- **C5 (P0).** **Verification ops survive reloads via persistence:** compile results are journaled to a
  file at `compilationFinished` (errors with file:line:column + assembly), so a reload-triggering refresh
  still yields its outcome to a follow-up query. (Working reference implementation exists — the XnaR
  AgentBridge `compile.json`.)
- **C6 (P1).** Length-prefixed JSON framing on TCP (no newline-fragility), UTF-8, documented max message
  size with chunked/paged alternatives for big results.
- **C7 (P1).** Long-running ops (test runs, future builds) return a job id; progress is pollable;
  completion delivers the full result; jobs survive *server* restarts (journaled editor-side) though not
  necessarily editor reloads (state which, per op).
- **C8 (P1).** Lifecycle hygiene: clean shutdown on editor quit; the npx server exits when its MCP client
  disconnects (no zombie processes); idempotent re-launch.

## D. The Serialization Core — the fork's depth identity (the centerpiece)

- **D1 (P0).** **`SerializedMemberHandler` built on `SerializedObject`/`SerializedProperty`** — never
  C# reflection — targeting ANY `UnityEngine.Object`: scene components, **ScriptableObject assets**,
  materials, asset importers.
- **D2 (P0).** Target addressing: scene object by hierarchy path or instance id; asset by **GUID or
  project path**; component by type name + index (for multiples); sub-assets addressable.
- **D3 (P0).** Property addressing by Unity's own `propertyPath` grammar, including `Array.data[i]`
  segments and nesting (`_states.Array.data[2]._gate`).
- **D4 (P0).** **Introspection first:** a tool that returns the full serialized property tree of a target
  (paths, `SerializedPropertyType`s, current values, array sizes, managed-reference type names) — agents
  must be able to DISCOVER structure, not guess it.
- **D5 (P0).** Read/write every relevant `SerializedPropertyType`: ints/floats/bools/strings, enums (by
  name and by index), LayerMask, Vector2/3/4, Rect, Bounds, Quaternion (+ euler convenience), Color (incl.
  HDR), AnimationCurve (keyframe array JSON), Gradient, ArraySize, and **ObjectReference resolved by GUID /
  asset path / scene hierarchy path** (null assignable).
- **D6 (P0).** **Private `[SerializeField]` fields work by construction** (the pipeline ignores C#
  visibility) — with an explicit test proving it, since this is the headline fix over the base.
- **D7 (P0).** **`[SerializeReference]` support:** read `managedReferenceFullTypename`; write via
  `managedReferenceValue` with type instantiation from an assembly-qualified name; null assignment; nested
  writes into managed-reference subtrees. Document 2020.3-specific quirks discovered.
- **D8 (P0).** Array/list operations: resize, insert-at, remove-at, move, clear — bounds-checked with
  structured errors.
- **D9 (P0).** **Correctness semantics, Inspector-equivalent:** `Undo.RecordObject` (or
  `RegisterCompleteObjectUndo` where required), `ApplyModifiedProperties` (an explicit `withoutUndo`
  variant), `EditorUtility.SetDirty`, **`PrefabUtility.RecordPrefabInstancePropertyModifications`** on
  prefab instances, and an explicit save policy: writes dirty, a separate `save_assets` tool persists
  (batch-friendly; never auto-save per write).
- **D10 (P1).** Batch edit: many property writes across many targets in one command — one Undo group, one
  dirty/save pass, all-or-nothing option.
- **D11 (P1).** Dry-run/validate mode: report what would change, with type-mismatch diagnostics, before
  mutating.
- **D12 (P1).** The legacy reflection path (the base's current core) demoted to a clearly-named fallback
  tool (`set_member_reflection`) or removed once D1–D9 land; never the default.

## E. Asset & Prefab Operations — legacy-aware correctness

- **E1 (P0).** ScriptableObject asset lifecycle: `CreateInstance` by type name → save at path; duplicate;
  rename/move via `AssetDatabase.MoveAsset`; delete with a dependents warning (see E5).
- **E2 (P0).** Prefab operations correct on 2020.3: open/close prefab stage (guarded namespace, B2); edit
  within the stage via D1; create prefab from scene object; instantiate prefab (as prefab instance, not
  clone); unpack (regular/complete); variant creation; apply/revert instance overrides — including
  per-property granular apply.
- **E3 (P0).** **GUID/.meta discipline:** never hand-write GUIDs or bypass `AssetDatabase` for file
  operations (orphaned-meta prevention); provide `guid_for_path` / `path_for_guid` lookups.
- **E4 (P1).** Import settings: read/modify importers (texture/model/audio) via `SerializedObject` +
  `SaveAndReimport`; platform overrides addressable.
- **E5 (P1).** Dependency queries (keep + harden the base's): dependencies and dependents of an asset, with
  depth control and result paging.
- **E6 (P1).** Scene file ops: open/save/new (additive supported), dirty-state query, active scene,
  build-settings scene list read/modify; all scene mutations refuse in play mode with a structured error.

## F. Scene & GameObject Operations — inherit the breadth, harden it

- **F1 (P0).** Audit all 20 inherited handler categories; each must pass: structured-error contract,
  play-mode safety behavior defined, Undo correctness, at least one matrix smoke test (B6).
- **F2 (P1).** Query tools with **paging and limits** (large legacy scenes must not blow MCP message
  budgets): find by name/path/tag/layer/component-type; hierarchy dumps depth-limited.
- **F3 (P1).** Transform ops with explicit local/world space parameters.
- **F4 (P1).** Component add/remove/reorder with `RequireComponent` awareness.
- **F5 (P1).** **Missing-script detection** (a legacy-project staple): report missing MonoBehaviours per
  scene/prefab, optional cleanup with confirm flag.

## G. Verification & Test Tooling — the agent-workflow core

- **G1 (P0).** `TestRunnerHandler` via `TestRunnerApi`: EditMode + PlayMode; filters (full names,
  categories, assemblies); per-test status/duration/message/stack-head; job semantics per C7; NUnit XML
  artifact export option. (Working EditMode reference exists — the XnaR AgentBridge test runner.)
- **G2 (P0).** Compile/refresh tool: trigger refresh, await compilation, return errors/warnings with
  file:line:column + assembly (C5's journal as the source); plus a "compile status since last reload" query.
- **G3 (P0).** Console tool: read with filters (log type, regex, count, since-token), clear, duplicate
  collapse; stack traces for errors/exceptions.
- **G4 (P1).** Play-mode controls: enter/exit/pause/step with state confirmation and
  `EnterPlayModeOptions` awareness (domain-reload-on-play differences across versions).
- **G5 (P1).** Capture: game view, scene view, specific camera; structured failure when no view exists
  (headless/CI).
- **G6 (P0).** Menu-item execution + **static-method invoke** (type + method by name, JSON-typed args) —
  documented as THE extension seam: project-side utilities are first-class, the bridge stays dumb transport.
- **G7 (P0).** Environment discovery in one call: Unity version, render pipeline + version, active build
  target, scripting defines, installed packages with versions. (Agents must orient before acting.)

## H. Security & Safety

- **H1 (P0).** Localhost-only binding by default; optional shared-secret token (env) on the TCP channel.
- **H2 (P0).** Allow/deny policy for menu execution and static invoke (config asset; default-deny for
  destructive menus — Build, asset deletion menus).
- **H3 (P0).** Destructive operations (asset delete, scene discard, override revert-all) require an
  explicit `confirm: true` parameter; dry-run defaults where sensible (D11).
- **H4 (P0).** Path sandboxing: all file-touching ops constrained to the project folder; no `..` escapes;
  asset operations exclusively via `AssetDatabase`.
- **H5 (P1).** Audit log option: every mutating command appended to a local journal (op, target, time) for
  post-hoc review.

## I. MCP Server (Node) Quality

- **I1 (P0).** Current MCP spec compliance; every tool has a JSON Schema and a **carefully written
  description** (agent success rates live and die on tool descriptions — review each one for
  actionability, parameter examples included).
- **I2 (P1).** Tool taxonomy + category filtering (env vars) so clients can load slim toolsets; consistent
  `verb_noun` naming.
- **I3 (P0).** Structured errors everywhere: machine code + human message + remediation hint (e.g.
  `editor_compiling` → "retry when status.isCompiling = false").
- **I4 (P1).** Server config: project targeting (C3), per-op-class timeouts, log file with rotation,
  `--version` printing server + connected-package versions.
- **I5 (P1).** Server test suite: protocol-level tests against a mocked editor; one real-editor
  integration smoke in the CI matrix.

## J. Documentation & DX

- **J1 (P1).** Per-tool reference generated from the schemas (no hand-maintained drift — same principle as
  A2).
- **J2 (P0).** Quickstarts per client (Claude Code `.mcp.json`, Claude Desktop, Cursor) — **always pinned
  versions in examples, never `@latest`**.
- **J3 (P1).** Troubleshooting matrix of the known failure smells with diagnosis steps: port conflict /
  wrong editor, reload disconnects, `isCompiling` waits, version mismatch, capability-negotiation refusals.
- **J4 (P2).** Migration guide from both upstream names (`unity-editor-mcp`, the deprecated fork) to the
  new scope.

## K. Stretch (P2)

- **K1.** uGUI automation hardening on 2020.3 (the inherited UIInteractionHandler); UI Toolkit runtime
  panels guarded per B1.
- **K2.** Scene/prefab snapshot + diff (hierarchy + serialized-state hash) as an agent verification aid.
- **K3.** Asset reference-finder: who references this GUID across scenes/prefabs (text-scan based).
- **K4.** Build trigger (H2-gated) surfacing `BuildReport` summaries.
- **K5.** Headless/batchmode operation of the bridge for CI usage.
- **K6.** File-protocol fallback transport for the verification subset (compile/test/console) — fully
  reload-proof channel beside TCP (the XnaR AgentBridge pattern, generalized).

---

## Seeds already in hand (carry into the fork as its first commits)
1. The `v1.3.1` floor-truth audit + the floor-bump archaeology (`8237fd8`) — the first COMPATIBILITY.md entry.
2. The 4-line PrefabStage patch (convert to the B2 guard).
3. The XnaR AgentBridge: TestRunnerApi runner (G1 EditMode core), compile journal (C5/G2), console tail
   (G3), invoke_static (G6), heartbeat/status (C4) — working 2020.3 reference implementations.
4. Today's failure catalog → J3's troubleshooting matrix, written from lived diagnosis.
