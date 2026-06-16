# Design — Semantic Code Intelligence (0.6.0): lite core + capability-gated Roslyn

Status: **Proposed (v2 — incorporates the 2026-06-16 multi-agent design review: 31 confirmed findings
folded in)**. Brainstormed in-session. Supersedes the "semantic layer is a later milestone" deferral in
`2026-06-15-field-projection-design.md`.

## 1. Context & goal

v0.4.0 shipped a **syntactic** analyzer (`Editor/Handlers/CodeIntelligenceHandler.cs` — `get_symbols` /
`find_symbol` / `find_references` / `get_symbol_body`, regex + brace-matching over comment/string-masked
source). It cannot **bind** symbols: "references" are textual matches; overloads/same-named members aren't
disambiguated; there is no type information.

0.6.0 adds **semantic** resolution, under the fork's hard constraints:

- Editor C# is **C# 8 / netstandard 2.0 / Mono** on the 2020.3 floor.
- Unity ships its **own** `Microsoft.CodeAnalysis`, **version-locked + divergent per editor** (3.8 on
  2020.3, ~4.3 on 2022.3, newer on 6000); referencing a NuGet Roslyn in an editor assembly conflicts
  (`Could not load file or assembly 'Microsoft.CodeAnalysis, Version=…'`). In-process modern Roslyn is a
  version-conflict minefield; using Unity's own copy turns the Roslyn **API surface** into a `#if` matrix.
- The Node server is **pure JS, `npx`-friction-free** (B5).

**Why we diverge from the siblings.** akiojin's forks bundle a .NET Roslyn sidecar (CodeAnalysis 4.11,
net9/10) that parses source out-of-process. For *this* fork that is (a) Unity-version-agnostic → no floor
edge; (b) commodity tooling that duplicates the siblings + IDEs; (c) a .NET-runtime tax against
friction-free. So Roslyn here is **opt-in and removable**, with a **Unity-grounded lite layer** as the
always-on default — the one semantic surface only the in-editor bridge can offer.

## 2. Architecture — two layers, capability-gated

### Layer 1 — Lite (always-on, in-editor, zero-dependency)

In-editor C#, grounded in Unity's **actual compiled + loaded state**, combining the existing syntactic
analyzer (positions, outline, textual refs) with **name → metadata resolution via reflection +
`UnityEditor.TypeCache`** over the loaded assemblies.

**What it can do — and the honest boundary (review finding #1).** Reflection/`TypeCache` resolve by
**name and type relationship**, *not* by source position. `TypeCache` is keyed by type/attribute
(`GetTypesDerivedFrom`, `GetTypesWithAttribute`, …); compiled metadata has no source-location index. So:

- `resolve_symbol` takes an **identifier name** (the syntactic layer extracts the token at a given
  file/position, but only to get the *name*) and returns **all matching types/members across loaded
  assemblies**, with kind/visibility/signature — a **ranked candidate list, not a unique binding**.
  Same-named members across types are *not* disambiguated here (that needs Roslyn).
- `get_type_members` — full member list of a *named* type (fields/props/methods/events, signatures,
  visibility, attributes). Exact.
- `find_implementations` — subtypes / interface implementors of a *named* type (`TypeCache`). Exact.
- go-to-**declaration** of a named type/method/field; overload signatures.

**Cannot do (no source binding):** rename, cross-file **semantic** find-references, expression /
local-variable type resolution, compiler diagnostics. Floor-safe: reflection + `TypeCache` are
netstandard2.0 / C# 8 / 2020.3-clean; ships nothing.

### Layer 2 — Roslyn (opt-in, out-of-process, removable)

A .NET Roslyn sidecar, **not bundled**, activated per-instance at runtime.

- **Packages:** `Microsoft.CodeAnalysis.CSharp` **and `Microsoft.CodeAnalysis.Workspaces`** — `SymbolFinder`
  (semantic find-refs) and `Renamer` live in *Workspaces*, not in `.CSharp` (review #2). The sidecar builds
  an `AdhocWorkspace`/`Project` so those APIs are available.
- **Process:** a standalone .NET process (its own modern Roslyn), **spawned + lifecycle-managed by Node**,
  one per active instance. The editor's Mono cannot host it (version conflict), so Node owns it. **IPC =
  newline/length-framed JSON-RPC over the child process's stdio** (review #12/#16): no network socket, so no
  localhost-binding or channel-auth question arises (review #15). stderr is the sidecar's log channel.
- **Project model (review #10/#11/#21):** `UnityEditor.Compilation.CompilationPipeline.GetAssemblies()`
  yields, per assembly: source file **paths**, compiler **defines**, and **paths to referenced `.dll`s**
  (`assembly.allReferences`) + the asmdef graph. It does **not** hand over live `MetadataReference`
  objects — the editor exports **paths**, and the sidecar reconstructs references with
  `MetadataReference.CreateFromFile(path)` and parses the source files itself. (Honest scope: defines +
  asmdef graph are Unity-exact; the references resolve to the same compiled dlls a `.csproj` would — the
  edge over the siblings is the Unity-exact *defines/graph*, not the dll set.)
- **Transport of the model (review #4 — the 1 MB framing cap is hard in both directions,
  `Framing.cs:17` / `unityConnection.js:282`, no per-command override):** the model is **NOT** sent over
  the framed channel. The editor **writes it to a temp file** (`Library/UnityEditorMCP/roslyn-model.json`,
  tagged with a compilation generation id) and returns only that **path + generation id** over the framed
  channel (tiny). The sidecar reads the temp file directly (same machine, filesystem access).
- **Error-tolerance (review #19):** Roslyn compiles partial/errored code natively — the sidecar always
  builds a `Compilation` and surfaces compiler errors as `get_diagnostics` output; it never refuses to load
  a project that doesn't compile.
- **Language version (review #18):** the sidecar parses with the **`LangVersion` carried in the exported
  model** (derived from the editor's C# version / defines), not the sidecar's default, so the parser matches
  the project.
- **Adds:** semantic `find_references`, `goto_definition` (overload-resolved), `rename_symbol`
  (dry-run + atomic apply), `get_diagnostics`, `get_type_hierarchy`.

## 3. The dynamic capability surface — the mechanism (review cluster A: #5/#6/#7/#8/#13/#20/#22/#25/#26)

The v0.5.0 surface is static (`editorToolSurface` freezes `conn.editorInfo` at handshake; `NODE_LOGIC_TOOLS`
is a static name map; `check-drift.mjs:30` hard-rejects any `sides` ∉ `{server, editor}`). The gate needs
real machinery, specified here:

1. **`RoslynManager` (new, Node)** — the per-instance sidecar-state store the codebase lacks today: a
   `Map<instanceKey, { state: 'off'|'indexing'|'ready'|'unavailable', client, modelGen }>`, analogous to
   `UnityConnectionManager`. `start_roslyn`/`stop_roslyn`/`roslyn_status` operate it; everything else
   *reads* it. This is where per-instance state lives — **not** in `NODE_LOGIC_TOOLS` (which stays a static
   map of *handlers*; the handlers consult the manager for instance state, resolving review #8).

2. **Catalog representation — no new `sides` value** (review #5/#13/#22): the drift gate only accepts
   `server`/`editor`, and a `server`-side command is validated by the presence of a **Node handler**. So:
   - The **lifecycle** commands (`start_roslyn`/`stop_roslyn`/`roslyn_status`) and the **sidecar-only**
     commands (`goto_definition`/`rename_symbol`/`get_diagnostics`/`get_type_hierarchy`) are cataloged
     **`sides: ["server"]`** with Node handlers that proxy to the sidecar (or return `ROSLYN_NOT_READY`).
     They never reach the editor → no editor dispatch case needed → drift is satisfied with **zero gate
     changes**. They carry a catalog marker **`requires: "roslyn"`** (the lifecycle three do not).
   - `find_references` stays **`sides: ["editor"]`** (its syntactic implementation) **and** is added to the
     Node-logic interception set so Node can route it (below). Same dual nature as the existing
     `create_script`/`analyze_screenshot`.

3. **Availability + discovery (review #7/#17/#20)** — `list_unity_tools(instance)` is extended to merge a
   **third source**, `mergeRoslynSurface(surface, instance, roslynManager)`, which **always lists** the
   `requires:"roslyn"` commands but annotates each with **`requires: "roslyn"`** and **`available:
   <RoslynManager.isReady(instance)>`**. Always-listing them (not hiding them) is deliberate: it's how the
   agent *discovers* that semantic ops exist and that `start_roslyn` unlocks them — solving the
   agent-discovery gap (#17) without any tool-list mutation. `find_references`' descriptor advertises
   `resolution: "syntactic" | "semantic"` and which it will return for this instance right now.

4. **Routing (review #6/#26)** — both `call_unity_tool` *and* the `list_unity_tools` availability check read
   `RoslynManager`:
   - `find_references` (Node-logic): `ready` → proxy to the sidecar (`resolution: "semantic"`); else →
     forward to the editor (`resolution: "syntactic"`).
   - sidecar-only commands (Node-logic): `ready` → proxy to the sidecar; else → `ROSLYN_NOT_READY` with
     remediation ("call `start_roslyn`").
   - `CallUnityToolToolHandler`'s availability gate (today re-derived from `editorToolSurface`) must consult
     the same merged surface so it doesn't reject Roslyn commands as "not available" (review #26).

## 4. Command surface & contracts (0.6.0)

Params/results are sketched here and pinned in `protocol/catalog/commands.json` at plan time (review
#23/#24). All paths are project-relative `.cs` paths, validated by `PathSafety` (§6).

**Lite (always available; `sides: ["editor"]`):**

| command | params | result |
| --- | --- | --- |
| `get_symbols` *(existing)* | `path` | `{ symbols[] }` outline |
| `find_symbol` *(existing)* | `name`, `kind?` | `{ matches[] }` |
| `find_references` *(existing, extended)* | `name`, `path?`, `position?` | `{ refs[], resolution: "syntactic" }` |
| `get_symbol_body` *(existing)* | `path`, `symbol` | `{ source }` |
| `resolve_symbol` **(new)** | `name` (or `path`+`position` → token name) | `{ candidates[]: { type, member, kind, signature, visibility, assembly } }` — ranked, may be >1 |
| `get_type_members` **(new)** | `typeName` | `{ members[]: { name, kind, signature, visibility, attributes[] } }` |
| `find_implementations` **(new)** | `typeName` | `{ implementors[]: { type, assembly, kind } }` |

**Roslyn-gated (`sides: ["server"]`, `requires: "roslyn"`; always listed, `available` per instance):**

| command | params | result |
| --- | --- | --- |
| `find_references` *(upgraded route)* | as above | `{ refs[], resolution: "semantic" }` |
| `goto_definition` | `path`, `position` | `{ definition: { path, position, symbol } }` |
| `rename_symbol` | `path`, `position`, `newName`, `dryRun?` | `{ edits[]: { path, range, oldText, newText }, applied: bool }` |
| `get_diagnostics` | `path?` (else whole compilation) | `{ diagnostics[]: { severity, code, message, path, position } }` |
| `get_type_hierarchy` | `typeName` | `{ base[], derived[], interfaces[] }` |

**Lifecycle (`sides: ["server"]`):** `start_roslyn { instance }` → async `{ state }`;
`stop_roslyn { instance }`; `roslyn_status { instance }` → `{ state, modelGen, projectStats? }`.

## 5. Sidecar lifecycle, safety & provenance

- **`start_roslyn`** → resolve instance → require it **live** → editor writes the model temp file → Node
  spawns the sidecar (if not running) → sidecar reads the model, builds the workspace → state
  `indexing` → `ready`. **Idempotent**; **async/pollable** via `roslyn_status` (the C7 job model). Returns
  `unavailable` (with remediation) if the backend isn't installed / no .NET / no model — **never crashes**;
  lite stays available.
- **`stop_roslyn`** + an **idle timeout** tear the sidecar down.
- **Binary provenance (review #14):** the sidecar is shipped as a **pinned, checksummed** artifact (npm
  `optionalDependencies` package or a versioned release download); Node **verifies the checksum before
  spawn** and runs only the bundled path — never an arbitrary binary. Absent backend → `unavailable`.
- **`rename_symbol` safety (review #29/#31):** cataloged **`destructive: true`**. `dryRun:true` returns the
  full edit set with **no writes**. Apply is **atomic** — stage all edits, write all-or-nothing (temp +
  rename, or in-memory validate then flush); a mid-apply failure rolls back, never a half-rename. Each
  target path is `PathSafety`-checked.
- **Model staleness (review #3/#27/#30):** the model temp file carries a **generation id**; the editor
  bumps it on compile-finished. `roslyn_status` reports the live generation; the sidecar re-reads the model
  when the generation advances (Node signals it, or the sidecar watches the file). `start_roslyn` is the
  explicit re-sync. Stale-read is bounded to "since last compile," surfaced in `roslyn_status`.
- **Crash / removed backend:** Node detects sidecar exit → marks `unavailable`; lite still works; the agent
  can `start_roslyn` again.

## 6. Performance, limits & floor (review #9)

- **Lite:** O(file) syntactic + O(types) reflection; bounded by the existing `MaxFileBytes`/result caps.
  netstandard2.0 / C# 8 / 2020.3-clean; verified by `compat-lint` + live 2020.3/2021.3/2022.3 dogfood. No
  new `#if` guards expected.
- **Sidecar:** Roslyn loading a large legacy solution is the real cost (seconds-to-minutes, hundreds of MB).
  Mitigations: indexing is **async + pollable**; `roslyn_status` reports project size; a **configurable
  ceiling** (max source files / memory) returns `unavailable` with a clear reason rather than OOM-ing the
  machine; the sidecar is **opt-in**, so the floor default never pays this. The sidecar is out-of-process
  with its **own** .NET — **not** subject to the editor floor and **not** Unity-version-coupled (the
  version-conflict problem never arises).
- The base install's floor-trueness + pure-JS promise are **untouched** (Roslyn optional + removable).

## 7. Testing

- **Lite:** NUnit EditMode tests for the resolution commands over fixtures (incl. the *ambiguous* case —
  `resolve_symbol` returning multiple candidates); floor-dogfooded on 2020.3/2021.3/2022.3 via the bridge;
  Node unit tests for the surface incl. the `resolution` tag.
- **Capability framework:** Node unit tests for `RoslynManager` + `start/stop/roslyn_status` (spawn/stop/
  status, the `unavailable` path, per-instance isolation, the `list_unity_tools` `available` annotation,
  `call_unity_tool` routing/`ROSLYN_NOT_READY`) against a **stubbed** sidecar.
- **Sidecar:** xUnit over `dotnet/UnityEditorMCP.Roslyn` (semantic ops, error-tolerant load, atomic rename,
  model-temp-file parse) — a sibling of the existing `dotnet test` lane.
- **Drift:** the new commands enter the catalog as `sides:["server"]` (+ `find_references` stays `editor`);
  regen `CommandCatalog.g.cs`; `check-drift` passes with **no gate changes**.

## 8. Scope & phasing (input to the implementation plan)

- **0.6.0 core (must):** Layer 1 lite (`resolve_symbol`/`get_type_members`/`find_implementations` + the
  `resolution` tag) **+** `RoslynManager` + the lifecycle commands **+** the editor model export
  (temp-file) **+** the merged capability surface. **Lite ships standalone value even if the sidecar slips**
  — and `start_roslyn` returns a clean `unavailable` until the sidecar exists, so the core never advertises
  a *dead* capability (review #scope): the gated commands list with `available:false` + remediation.
- **0.6.0 sidecar MVP (may split to 0.6.x):** the .NET sidecar + semantic `find_references` +
  `goto_definition` + `get_diagnostics`. `rename_symbol` + `get_type_hierarchy` = a second sidecar wave.

## 9. Decisions resolved

- Lite always-on, no .NET; Roslyn **opt-in + removable**; base stays `npx`-friction-free.
- `resolve_symbol` is **name-based** (ranked candidates, ambiguity disclosed) — position only extracts the
  token name; it does **not** promise source-level binding (review #1).
- Project model = **editor-exported `CompilationPipeline`**, serialized as **paths + defines + asmdef graph
  to a temp file** (not the framed channel), reconstructed sidecar-side via `MetadataReference.CreateFromFile`
  (review #4/#10/#11). Requires a **live** instance; editor-closed analysis deferred.
- Sidecar IPC = **stdio JSON-RPC** (no network → no auth/localhost question); packages include
  **Workspaces**; parses with the project's **`LangVersion`**; **error-tolerant** load.
- Sidecar-only + lifecycle commands = **`sides:["server"]`** + Node proxy handlers (+ `requires:"roslyn"`
  marker); `find_references` stays **`sides:["editor"]`** + Node-intercepted. **No drift-gate change.**
- Per-instance state lives in a new **`RoslynManager`**; `list_unity_tools` + `call_unity_tool` both consult
  it; gated commands are **always listed** + annotated `available` (discovery + dynamic surface in one).
- `rename_symbol` = **`destructive:true`**, dry-run + **atomic** apply, `PathSafety`-checked.

## 10. Non-goals (0.6.0)

- Bundling Roslyn / making it default. Editor-closed semantic analysis. A full LSP wire protocol (we expose
  targeted agent commands). Competing with Rider/VS for human-facing refactoring. Remote/non-loopback
  sidecar (the model temp file + stdio assume same machine).
