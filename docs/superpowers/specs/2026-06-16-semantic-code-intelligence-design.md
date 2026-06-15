# Design — Semantic Code Intelligence (0.6.0): lite core + capability-gated Roslyn

Status: **Proposed** — 2026-06-16. Brainstormed in-session. Supersedes the "editor-sourced Roslyn /
semantic layer is a later milestone" deferral noted in `2026-06-15-field-projection-design.md` and the
`CodeIntelligenceHandler` header ("a full semantic layer is a separate opt-in milestone").

## 1. Context & goal

v0.4.0 shipped a **syntactic** analyzer (`Editor/Handlers/CodeIntelligenceHandler.cs` —
`get_symbols` / `find_symbol` / `find_references` / `get_symbol_body`, regex + brace-matching over
comment/string-masked source). It cannot **bind** symbols: "references" are textual identifier matches,
overloads/same-named members aren't disambiguated, and there is no type information.

0.6.0 adds **semantic** resolution, under the fork's hard constraints:

- Editor C# is **C# 8 / netstandard 2.0 / Mono** on the 2020.3 floor.
- Unity ships its **own** `Microsoft.CodeAnalysis`, **version-locked and divergent per editor** (3.8 on
  2020.3, ~4.3 on 2022.3, newer on 6000) — referencing a NuGet Roslyn in an editor assembly triggers
  `Could not load file or assembly 'Microsoft.CodeAnalysis, Version=…'`. In-process modern Roslyn is a
  version-conflict minefield; using Unity's own copy turns the Roslyn **API surface** into a `#if` matrix.
- The Node server is **pure JS, `npx`-friction-free** (requirement B5).

**Why we diverge from the siblings.** akiojin's `unity-mcp-server` / `unity-cli` (forks of the same
upstream) added Roslyn as a **bundled .NET sidecar** (`Microsoft.CodeAnalysis.CSharp` 4.11, net9/net10)
that parses `.cs` source out-of-process. That is the proven full-semantic path, but for *this* fork it is:
(a) **Unity-version-agnostic** — it reads source, so it gives the floor no edge; (b) **commodity C#
tooling** that duplicates the siblings and the IDEs; and (c) a **.NET-runtime tax** against
`npx`-friction-free. So we make Roslyn **opt-in and removable**, and keep a **Unity-grounded lite layer**
as the always-on default — the one semantic surface only the in-editor bridge can offer.

## 2. Architecture — two layers, capability-gated

### Layer 1 — Lite (always-on, in-editor, zero-dependency)

The floor default. In-editor C#, grounded in Unity's **actual compiled + loaded state**, combining the
existing syntactic analyzer (positions, outline, textual refs) with **new symbol resolution via
reflection + `UnityEditor.TypeCache`** over the compiled assemblies (`Library/ScriptAssemblies` / the
loaded AppDomain).

- **Can do (metadata-grade):** resolve an identifier → declaring type + member kind + signature; full
  member listings (fields/props/methods/events) with signatures, return types, visibility, attributes;
  inheritance + interface implementors ("who implements `X`", "subtypes of `Y`"); go-to-**declaration**
  of a type/method/field; overload signatures.
- **Cannot do (no source binding):** rename, cross-file **semantic** find-references, expression /
  local-variable type resolution, compiler diagnostics.
- **Floor:** reflection + `TypeCache` are netstandard2.0 / C# 8 / 2020.3-clean. Ships nothing.

### Layer 2 — Roslyn (opt-in, out-of-process, removable)

A .NET Roslyn sidecar that is **NOT bundled** — absent from the base install, activated per-instance at
runtime.

- **Distribution:** optional component (separate download / npm `optionalDependencies` / self-contained
  per-OS binary). The base npm package carries **no .NET footprint**; uninstalling it leaves lite intact.
- **Process:** a standalone .NET process (modern `Microsoft.CodeAnalysis.CSharp` + `…Workspaces`),
  **spawned and lifecycle-managed by the Node server**, one per active instance/project. The editor's Mono
  cannot host it (version conflict), so Node owns it.
- **Project model — the Unity-native differentiator:** the sidecar does **not** guess from a disk
  `.csproj`. On `start_roslyn`, the **live editor exports its real `CompilationPipeline` model** — source
  files + the exact `MetadataReference` set + scripting defines + asmdef graph — and the sidecar builds a
  Roslyn `Compilation` from it. This is grounded in Unity's *actual* compilation, **more accurate than the
  siblings' csproj-parsing LSP** (no stale/guessed references). It does require the instance to be live.
- **Adds:** semantic `find_references`, `goto_definition` (overload-resolved), `rename_symbol` (cross-file,
  dry-run + apply), `get_diagnostics` (compiler errors/warnings), `get_type_hierarchy`.

### The capability gate (the Adaptive Handshake, pioneered here)

- `start_roslyn(instance)` → resolve instance → require it live → export the project model → spawn/attach
  the sidecar → return **async** status (`indexing` → `ready`, or `unavailable`). Idempotent.
- `stop_roslyn(instance)` + an idle timeout → tear the sidecar down.
- `roslyn_status(instance)` → `ready` / `indexing` / `unavailable` (+ reason) + project stats.
- Once `ready`, the instance's surface — via the v0.5.0 `list_unity_tools` — **advertises** the semantic
  commands and the upgraded resolution; the agent invokes them through `call_unity_tool`. **No MCP
  tool-list mutation** — the same on-demand discovery as 0.5.0; the per-instance capability set simply
  *grows*. This is the **Adaptive Capability Handshake** (the 1.x flagship) in concrete, shipped form.
- The semantic commands live in the **protocol catalog** (the static contract, drift-checked like any
  command); `list_unity_tools(instance)` is what marks them **available on this instance** — only when
  Roslyn is `ready`. Catalog = the universe of commands; the per-instance surface = what this editor can
  do right now. (Same distinction 0.5.0 already draws.)

## 3. Command surface (0.6.0)

**Lite (always available; `sides: ["editor"]`):**

| command | status | what |
| --- | --- | --- |
| `get_symbols` | existing | file outline |
| `find_symbol` | existing | by name / kind |
| `find_references` | existing → tagged | textual refs; result carries `resolution: "syntactic"` |
| `get_symbol_body` | existing | extract a symbol's source |
| `resolve_symbol` | **new** | identifier at path/position → declaring type + member kind + signature |
| `get_type_members` | **new** | full member list of a type (sig, visibility, attributes) |
| `find_implementations` | **new** | subtypes / interface implementors (`TypeCache`) |

**Roslyn-gated (advertised only when `ready`):**

| command | what |
| --- | --- |
| `find_references` (upgraded) | same command; result `resolution: "semantic"` (true symbol refs) |
| `goto_definition` | exact symbol, overload-resolved |
| `rename_symbol` | cross-file safe rename (`dryRun` → preview; apply) |
| `get_diagnostics` | compiler errors/warnings for a file/project |
| `get_type_hierarchy` | base / derived / implemented across the compilation |

`find_references`' `resolution` field is the **agent-transparency contract**: the agent knows whether a
result is trustworthy enough to drive a rename (semantic) or only a hint (syntactic).

## 4. Routing & where each piece lives

- **Pure-lite commands** (`resolve_symbol`, `get_type_members`, `find_implementations`, `get_symbols`,
  `get_symbol_body`, `find_symbol`) — plain **editor** commands on the Core dispatcher rail
  (`CodeIntelligenceHandler` extended). `sides: ["editor"]`.
- **`find_references`** and the **semantic commands** — **Node-logic routed** (the v0.5.0
  `NODE_LOGIC_TOOLS` pattern): Node owns the sidecar, so it routes per state — *sidecar if `ready`
  (semantic), else forward to the editor (syntactic)* for `find_references`; semantic-only commands route
  to the sidecar or return `ROSLYN_NOT_READY`.
- **`start_roslyn` / `stop_roslyn` / `roslyn_status`** — **Node-logic** (Node spawns/manages the sidecar).
  The project-model export they trigger is an **editor** command Node calls.
- **The sidecar** — a new dotnet sub-project (`dotnet/UnityEditorMCP.Roslyn/`), modern Roslyn, speaks the
  length-prefixed JSON framing (or stdio JSON-RPC) to Node; xUnit-tested like `UnityEditorMCP.Core.Tests`.

```
Agent → call_unity_tool(instance, <lite cmd>)      → Node → editor (reflection/TypeCache + syntactic) → result
Agent → call_unity_tool(instance, start_roslyn)    → Node spawns sidecar; editor exports CompilationPipeline model
                                                      → sidecar builds Compilation (indexing → ready)
Agent → call_unity_tool(instance, <semantic cmd>)  → Node routes to the SIDECAR → semantic result
```

## 5. Error handling & edge cases

- `start_roslyn` with the backend **absent** → `unavailable` + remediation (how to install); **never a
  crash**, lite stays available.
- `start_roslyn` with the instance **not live** → error (a running editor is required for the model export).
- **Large legacy project** indexing → async, pollable via `roslyn_status` (the C7 job model).
- **Sidecar crash** → Node detects, marks `unavailable`, lite still works; the agent can restart.
- **Model staleness** → re-export on recompile (editor signals `isCompiling`/compile-finished) or on demand.
- **Removed backend** → every Roslyn-gated command yields `ROSLYN_NOT_READY`; lite is unaffected.

## 6. Floor & compatibility

- **Lite:** reflection + `TypeCache` only — netstandard2.0 / C# 8 / 2020.3-clean. No new `#if` guards
  expected; verified by `compat-lint` + live 2020.3/2021.3/2022.3 dogfooding.
- **Sidecar:** its **own** modern .NET, out-of-process — **not** subject to the editor floor and **not**
  Unity-version-coupled. The version-conflict problem never arises (it never loads Unity's Roslyn).
- The base install's floor-trueness and pure-JS promise are **untouched** — Roslyn is optional + removable.

## 7. Testing

- **Lite:** NUnit EditMode tests for the resolution commands over fixtures (`HandlerSecurityTests`-style),
  floor-dogfooded on 2020.3/2021.3/2022.3 via the bridge; Node unit tests for the surface.
- **Capability framework:** Node unit tests for `start/stop/roslyn_status` (spawn/stop/status, the
  `unavailable` path) against a **stubbed** sidecar.
- **Sidecar:** xUnit over `dotnet/UnityEditorMCP.Roslyn` (semantic ops on fixtures), a sibling of the
  existing `dotnet test` lane.
- **Drift:** new commands enter `protocol/catalog/commands.json`; regen `CommandCatalog.g.cs`; `check-drift`.

## 8. Scope & phasing (input to the implementation plan)

- **0.6.0 core (must):** Layer 1 lite (`resolve_symbol` / `get_type_members` / `find_implementations` +
  the `resolution` tag on `find_references`) **+** the `start_roslyn` / `stop_roslyn` / `roslyn_status`
  capability framework **+** the editor project-model export. **Lite ships value on day one even if the
  sidecar slips.**
- **0.6.0 sidecar MVP (may split to 0.6.x):** semantic `find_references` + `goto_definition` +
  `get_diagnostics`. `rename_symbol` + `get_type_hierarchy` can be the second sidecar wave.

## 9. Decisions resolved

- Lite always-on, no .NET; Roslyn **opt-in + removable**. The base stays `npx`-friction-free.
- Project model = **editor-exported `CompilationPipeline`** (Unity-accurate), **requires a live instance**.
  Editor-closed semantic analysis is deferred.
- `find_references` is **one command**; a `resolution` field flags syntactic vs semantic. Semantic-only
  ops are distinct commands.
- The sidecar is **Node-spawned + Node-routed** (the editor's Mono can't host modern Roslyn).

## 10. Non-goals (0.6.0)

- Bundling Roslyn or making it the default.
- Editor-closed semantic analysis.
- A full LSP wire protocol (we expose targeted agent commands, not LSP).
- Competing with Rider/VS for human-facing refactoring.
