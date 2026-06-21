# Path-Sandbox Completeness Sweep (0.13.0) Design

> Status: design (autonomous, rule pre-agreed with the maintainer). Section H, requirement **H4**: "Path
> sandboxing — all file-touching ops constrained to the project folder; no `..` escapes; asset operations
> exclusively via AssetDatabase."

## 1. The containment rule (agreed)

A caller-supplied path (from `call_unity_tool` or a direct TCP client) that touches the filesystem must:
1. **Canonicalize** via `Path.GetFullPath` (collapses `..`, resolves relative against the **project root**, not the CWD).
2. **Stay under the project root** — the *whole* project folder (`Assets`, `ProjectSettings`, `Library`, `Packages`, `UserSettings`, in-project `Temp`, etc.), with a trailing-separator check to block sibling-prefix collisions (`C:/proj` vs `C:/proj-evil`).
3. **Asset operations go through `AssetDatabase`** (never raw file IO for assets — keeps GUID/.meta consistent); those are additionally `Assets/`/`Packages/`-scoped by the API.

`PathSafety.IsWithinProject` already implements 1+2. This slice is the **completeness sweep**: verify *every*
file-touching handler enforces it on caller-controlled paths, and fill any gap.

**Known limitation (documented, not fixed):** the check is on the canonical path *string*; it does not resolve
symlinks/junctions, so a pre-planted link inside the project pointing outside it could escape. Real link
resolution needs `LinkTarget`/P-Invoke unavailable on the Mono/2020.3 floor. Given the threat model (an LLM
agent already trusted with project writes; planting a junction requires out-of-project access the sandbox
already denies), this residual is accepted and noted.

## 2. Config defaults (decided for the record — H1/H2 are deferred)

- **Token (H1):** off by default; when added, supplied via env (`UNITY_MCP_TOKEN`) — a *channel* concern read
  by both Node and the editor, never committed to VCS.
- **Allow/deny (H2):** allow-all by default; when added, the *policy* lives in `ProjectSettings/` (versioned,
  shared) surfaced via a `SettingsProvider` window (no separate DLL; edited through `SerializedObject`).
- These are not implemented here — recorded so the later H1/H2 slice has its defaults + storage settled.

## 3. Scope of the sweep

Audit the file-touching surface (≈69 sites across 10 handlers: AssetDatabaseHandler, AssetManagementHandler,
AssetImportSettingsHandler, ScriptHandler, CodeIntelligenceHandler, SceneHandler, ScreenshotHandler,
EditorInfoHandler, CompilationHandler, RoslynModelExporter). For each site classify the path as **caller-
controlled** (from params), **derived** (built from a caller value), or **fixed** (internal constant, e.g. the
audit-log / roslyn-model Library paths). Every caller-controlled or derived path that reaches a file op must
pass `IsWithinProject` (and, for asset ops, route through `AssetDatabase`) **before** the op. Gaps are fixed.

## 4. Testability refactor

Extract the pure containment logic to Core so it is `dotnet`-tested: `Core.PathContainment.IsWithin(string
root, string candidate)` (canonicalize + under-root + sibling-prefix), with `PathSafety.IsWithinProject`
delegating to it using `Application.dataPath`. dotnet tests cover: `..` traversal escape, absolute-path escape,
sibling-prefix (`proj` vs `proj-evil`), a legitimate in-project relative + absolute path, empty/null, and the
root itself.

## 5. Out of scope

H1 token + H2 allow/deny enforcement (defaults decided above; implementation is a later slice). No transport
or config-window code here.

## 6. Testing

- **Core dotnet (`PathContainmentTests`):** the cases in §4.
- **Editor:** rely on the existing handler tests + the live bridge; spot-dogfood a `..`-escape path on a
  representative handler → rejected (`VALIDATION_ERROR`), and an in-project path → accepted.
- The sweep's gap fixes each keep the existing handler tests green (recompile + EditMode).
