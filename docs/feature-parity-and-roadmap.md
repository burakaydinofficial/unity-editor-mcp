# Feature Parity & Roadmap

> **Return-point document.** Written when development was paused after shipping **0.21.0** (2026-07-03). The project
> stays live, used, and maintained during the pause, but no new features are planned for ~1–2 weeks. This captures
> where the feature surface stands versus the sibling forks, what is intentionally deferred, and the sequenced plan
> for closing the gap when work resumes. See also `.claude/unity-mcp-fork-requirements.md` (the authoritative
> mission/requirements) and `CHANGELOG.md`.

## Positioning vs the sibling forks

All three projects fork the same upstream (`ozankasikci/unity-editor-mcp`) and then diverge:

| | **This fork** (`unity-editor-mcp`, burakk) | `unity-mcp-server` (akiojin, Node) | `unity-cli` (akiojin, Rust) |
|---|---|---|---|
| Status | **active**, v0.21.0 | **deprecated** (v5.5.2) | active, v0.11.4 |
| Agent integration | **MCP** (any MCP client) | MCP (+ HTTP) | **not MCP** — CLI / skills shell-out |
| Editor tools | 99 (+3 meta) | ~108 | ~129 |
| Unity floor | **2019.4** (guards-not-floors, CI 2019.4–2022.3) | contradictory (manifest 6000.0 / README 2020.3) | **Unity 6 only** (6000.0) |
| Hard package deps | Newtonsoft only (runs on a bare project) | Input System + Recorder + Addressables | Input System + Recorder + Addressables |
| Install | npx + git-URL UPM, **no native deps in base** | npx but native deps (sqlite, .NET LSP) | `curl \| sh` Rust binary + .NET LSP |
| Correctness discipline | **protocol contract + drift gate + dotnet-tested Core + 8-round audit** | monolithic handlers, single-in-flight queue | Rust tests, no wire-truth core seam |

**Our identity:** the *narrower-but-deeper, older-Unity, correctness-first, MCP-native* bridge. The akiojin line
(now `unity-cli`) is *broader, Unity-6-only, code-intelligence-heavy*, having traded MCP for a Rust CLI.

## Feature parity

Scored against the **union** of both akiojin projects' user-facing capabilities (their non-MCP CLI model, Docker,
and daemons excluded as design choices, not features). **have (1) / partial (0.5) / missing (0)** → **≈ 18/26 ≈ 69%**
(≈ 65–70% depending on category granularity). We are at ~100% of the *core editor-operation* surface; the gap is
concentrated in a few specialized areas.

### Have — parity or better (17)
scene · gameobject+hierarchy · component · prefab (variants/overrides/unpack) · asset-db/import/material/SO ·
play-mode · UI automation · **test-runner (lead)** · console/logs · editor-control (selection/tags/layers/windows/
tools/packages/settings) · menu + reflection invoke · screenshot+analyze · scene analysis/inspection · compilation
monitoring · **deep serialization (lead)** · multi-instance discovery · code-intelligence **read** (symbols/refs/
resolve/type-members/implementations)

### Partial (2)
- **Project-wide code index/search** — we have syntactic symbol search; they have a SQLite index / semantic search.
- **Dry-run / preview** — we have per-tool confirm-gates + serialization compare-and-swap previews, but no global
  `--dry-run` across all mutating tools.

### Missing (7) — the gap
1. **Structured code *editing*** — `rename_symbol` / `create_class` / `replace_symbol_body` / `edit_snippet`
   (ours is read-only). *Highest leverage; builds on our existing Roslyn read layer; no new Unity deps.*
2. **Input System automation** — input simulation (keyboard/mouse/gamepad/touch) + action-asset editing (14–17 tools there).
3. **Profiler** (start/stop/status/metrics).
4. **Addressables** (build/analyze/manage).
5. **Semantic reference search** over Unity's own C# source (embeddings) — `unity-cli` only.
6. **Video / recording capture** (we have screenshots only).
7. **Animator controller / animation-clip / sprite-atlas authoring.**

## Known limitations — intentionally deferred (do NOT treat as bugs)

- **Sec-4** — `capture_screenshot` / `analyze_screenshot` IO resolves relative paths against the process CWD; harmless
  because the editor's CWD *is* the project root, so guard + IO agree (a no-observable-change latent item).
- **Core-3b** — `enhanced_read_logs` `since`/`until` filters are ineffective because per-entry timestamps are
  fabricated at read time.
- **`clear_console`** — `clearOnRecompile` / `clearOnBuild` params are accepted but not applied (no stable EditorPref);
  the response now reports this honestly (`clearOnRecompileApplied: false`).
- **Unity 6 (6000.x)** — **6.0 is now CI-verified** (`6000.0.78f1` in the floor-matrix as of 0.21.0, 297/297 EditMode
  green); newer 6.x (6.1+) remain API-guarded but not in the matrix — add a host only if you want a newer-6.x ceiling.

## Roadmap — closing the gap (by impact per effort)

Everything below is floor-compatible with `#if` / optional-dependency guards, so a bare 2019.4 project keeps working.

1. **Structured code editing** — turn the read-only Roslyn intelligence read/write (`rename_symbol`,
   `replace_symbol_body`, `create_class`, `insert_after_symbol`, `remove_symbol`). Highest leverage, no new Unity deps.
2. **Input System automation** — behind an optional `com.unity.inputsystem` dependency + `INPUT_SYSTEM` guards
   (works back to 2019.4). Both simulation and action-asset editing.
3. **Profiler + Addressables** — Profiler API is ancient (fine on the floor); Addressables behind an optional package guard.
4. **Semantic reference search** over `UnityCsReference` — the largest / most specialized (embeddings + a cache);
   lower priority.
5. **Video/recording capture** (optional `com.unity.recorder`) and **Animator/animation authoring**.

## Robustness backlog — new-dimensions audit (2026-07-05)

A fresh adversarial audit on the dimensions the correctness campaign didn't cover (performance/scale, concurrency,
resource-exhaustion/DoS, attacker-model security, protocol fuzzing) found 12 issues. The **bounded** ones were fixed
immediately (Assets/-containment on `update_script`/`delete_script`/`read_script`; caps on `modify_serialized_array`
resize, `capture_screenshot` dims; `mesh.triangles`→`GetIndexCount`; Roslyn fetch timeouts; O(1) framing-recovery
scan; C# reapStale TOCTOU). These **need design/refactor** and are deferred (not bugs in shipped behavior, just
scale/hardening gaps):

- **`get_object_references` uncapped deep scan** (`SceneAnalysisHandler`) — walks every property of every component of
  every scene object with `SerializedObject.Next(true)`; no node budget. Needs a budget/cap **without** breaking the
  "which objects are unreferenced" correctness (a naive cap gives wrong answers). The only find/analyze member with no cap.
- **`find_by_component` (searchScope all/prefabs)** loads + deep-scans **every** project prefab regardless of `limit`
  (the cap only trims output). An early-out changes result semantics (post-sort) — needs a deliberate "first-N-by-scan"
  vs "N-after-sort" decision.
- **Editor `TcpTransport`** — no connection cap and no idle/read timeout; a local peer can exhaust tasks/sockets with
  idle or slowloris connections. Needs a max-connections + read-timeout design.
- **Node framing** `Buffer.concat` on every `data` event — O(n²/chunk) copy while a large frame streams (bounded by the
  1MB cap, so low priority; a chunk-list + single concat fixes it).
- **Critic-flagged, unexamined:** `CommandQueue` unbounded enqueue + unbudgeted `DrainAll`; `McpBridge` JSON ingest has
  no `MaxDepth` (deep-nesting parse); asset-mutation handler containment parity (verify the asset handlers guard
  caller paths like the script/screenshot ones now do); `StaticInvokeHandler` return-value serialization edges.

## Package management + read-scope (design note, 2026-07-05)

Agents already add/update/remove packages via **`manage_packages`** (`UnityEditor.PackageManager.Client.Add/Remove`,
add-with-`name@version` = update) + **`list_packages`** — the safe API path. The round's script-containment fix
(`update_script`/`delete_script` → Assets-only) is **complementary**: it blocks corrupting `Packages/manifest.json` /
`.git/` by raw file write, while the Package Manager API stays the intended interface. Principle: mutate non-Assets
project state through **dedicated, validated tools**, never by loosening the general file read/write path (that
reopens the `.git`/secrets info-disclosure + write-anywhere holes). Polish for `manage_packages` when features resume:
an explicit `update` action + available-version query, an **H3 confirm-gate on `remove`** (its one destructive
action, currently ungated), `packageId` validation, and awaiting the async `Client` request to report real
success/failure instead of fire-and-forget. If a real need arises to READ other project folders (ProjectSettings,
build config), add a purpose-built reader with an allowlist rather than widening `read_script`.

## Hotfix release runbook (during the pause)

If a bug fix must ship while paused, the exact process that cut 0.21.0:

1. Fix on `main` (or a short-lived fix branch off `main`). Verify: EditMode (host
   `unity-editor-mcp/unity-test-projects/2022.3/2022.3`), `dotnet test`, `mcp-server` `npm run test:unit`,
   `node scripts/compat-lint.mjs`, `node protocol/scripts/check-drift.mjs`.
2. Bump **both** `unity-editor-mcp/package.json` and `mcp-server/package.json` (keep them equal), add a `CHANGELOG.md`
   entry, commit `release: X.Y.Z - ...`.
3. Fast-forward `main`, create + push tag `vX.Y.Z`, then `gh release create vX.Y.Z` (title + notes).
4. The **`publish-npm`** workflow auto-publishes to npm via OIDC Trusted Publishing (no token). The **floor-matrix**
   CI cold-compiles + EditMode-tests 2019.4–2022.3 on the tag. UPM ships by the git push (git-URL install).
