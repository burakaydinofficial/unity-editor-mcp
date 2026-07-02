# Live-Editor E2E Harness — Design

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:writing-plans` to turn this design into a
> task-by-task implementation plan. This document is the design of record.

**Status:** Approved design (2026-06-28) — pending implementation plan.

**Goal:** Give the editor tools that *cannot* run in the EditMode floor-matrix CI deterministic, outcome-verified
coverage by driving a **real Unity editor** from an external Node process, starting with the play-mode and
script/recompile flows.

**Non-goal:** replacing or extending the floor-matrix. The EditMode aftermath suite already covers the
testable-in-EditMode surface (~66/99 tools); this harness closes the *structurally* untestable remainder.

---

## 1. Problem — why these tools are untestable in the floor-matrix

The floor-matrix runs GameCI's `unity-test-runner`: **one headless editor in batch mode** runs the **EditMode
NUnit** assembly inside a **single app domain** and reports pass/fail. ~24 tools break that model three ways:

1. **A domain reload destroys the running test.** NUnit methods execute *inside* the editor's app domain. These
   tools trigger a domain reload that tears down and rebuilds that domain mid-test, so the test never reaches its
   assertions: `create/update/delete_script` + `refresh_assets` (compile → reload), `add/remove package` (resolve →
   reload), `play_game`/`stop_game` (play-mode transition → reload). `SessionState` tricks can persist a flag across
   a reload, but you cannot cleanly assert the before/after of a mutation whose entire effect *is* the reload.
2. **Re-entrancy / self-reference the in-process model can't give.** `run_tests` — the Test Runner is not
   re-entrant; a test cannot start the runner while the runner is running it. `quit_editor` — quits the very editor
   executing the suite; the process exits and nothing is left to read the outcome.
3. **Batch/headless + out-of-band.** Play-mode transitions in `-batchmode` do not pump the editor loop like a real
   window — observed to **hang/freeze** in this project. `handshake` is the private connect-time manifest the Node
   server sends on TCP connect, not a rail-dispatched command, so an in-editor NUnit test has nothing to invoke.
   `list_scripts` is near-trivial but only meaningful after a real authoring flow (which recompiles → reloads).

**Common thread:** each tool either **destroys the test's own runtime** (reload/quit) or **requires a second actor**
(re-entrant runner, out-of-band connect). *You cannot test a process disruption from inside the process being
disrupted.* The fix is an **external, reload-surviving observer** — a separate Node process that drives the editor,
rides through the reload/quit, and inspects the result from outside.

---

## 2. Decisions (validated in brainstorming)

- **Run target — local-first, CI-capable later.** A bridge-driven live editor cannot run in the current headless
  GameCI floor-matrix; solving headless play-mode driving up front is a large, flaky infra bet. Build a local
  harness that delivers the coverage now, structured so a self-hosted / headed CI runner can adopt it unchanged.
- **v1 scope — the two hardest, most-representative flows:** the **play-mode family** (`play_game`, `pause_game`,
  `stop_game`, `get_editor_state`) and **script CRUD + recompile** (`create_script`, `read_script`, `update_script`,
  `delete_script`, `list_scripts`, `refresh_assets`, `get_compilation_state`). They exercise both the
  reload-survival mechanism and the highest-risk tools, de-risking the harness before more tests are built on it.
- **Verification — mixed, version-robust.** Bridge read-back is the **primary** contract check; version-robust
  **independent channels** are the cross-check; **both** run on the false-success-prone assertions.
  - *Version-robustness rule (hard):* independent channels use **only** signals that are stable or that we control —
    file **existence**, the stable `error CS` **compile grammar**, and the probe's **own JSON schema**. Never parse
    serialized `.unity`/`.prefab`/`.asset` YAML or exact log prose (they drift across 2019 → 6000).

---

## 3. Architecture — topology & lifecycle

The harness drives the **real MCP client → server → editor chain** an agent uses (not a mock, not a direct-TCP
shortcut). Per run:

1. The **runner** launches **one headed editor** (real window — batch-mode freezes on play-mode entry) on a
   **dedicated `e2e-host` project**, waits for the bridge (`Editor.log` shows `TcpTransport listening on …`), then
   spawns the **MCP server** (`UNITY_PROJECT_PATH` = the host) and connects an **MCP client** to it. It drives tools
   via `list_unity_tools` / `call_unity_tool`.
2. **One editor boot per run.** Every tool-induced **domain reload** happens *inside* that single editor — a reload
   is seconds, **not** a reboot. The MCP server's existing auto-reconnect re-establishes its TCP to the editor
   after each reload; the harness simply **retries the tool call** until it answers post-reload.
3. **Warm `Library`, amortized.** The `e2e-host` is **persistent and git-ignored** with a **kept warm `Library`**.
   The brutal cold import (15–45 min with no `Library`) is paid **once, ever** — a one-time provisioning `-quit`
   warm-up — and the host is deliberately **minimal** (few packages/assets) so even that is as short as possible.
   Every subsequent run is a warm boot (seconds to a couple minutes).
4. **Isolation via cleanup, not reboots.** Each test cleans its own scratch (delete its scripts, exit play mode,
   destroy created objects) in a `finally`, exactly like the aftermath suite.
5. **`quit_editor` runs last** (deferred to a later scope, but the lifecycle already accounts for it — it ends the
   editor, so nothing follows). **Reboot only as crash-recovery**, never routine.

**Net:** ~1 editor boot per run for the core scope, warm `Library` reused across runs.

---

## 4. Components

Each is small and single-purpose.

| Component | Responsibility | Interface / deps |
| --- | --- | --- |
| `runner.mjs` | Provision-check the warm host, launch the headed editor, wait-for-bridge (log poll), spawn the MCP server + client, teardown, crash-recovery | env: `UNITY_PATH`, `E2E_HOST_PROJECT`, `E2E_TIMEOUT`; spawns Unity + `mcp-server` |
| MCP driver | Drive tools through the real chain; **retry across reloads** (tolerate a lost/timed-out response on a transition, then poll the outcome) | reuses the existing `tests/e2e` MCP-client pattern (`StdioClientTransport` → `mcp-server`), minus the mock |
| in-editor **probe** | One `Editor/` `[InitializeOnLoad]` script in the host that re-subscribes to `playModeStateChanged` / `pauseStateChanged` on every domain load and **appends** play-mode events to a scratch JSON in our own stable schema | writes `E2E_PROBE_FILE`; survives reloads |
| verify helpers | `fs` (file existence), `logCompile` (wraps `read-editor-log.mjs`'s `error CS` parser), `probe` (read the JSON), `readback` (bridge read tools) | pure Node; read-only |
| `e2e-host` project | Persistent, warm-`Library`, minimal Unity project the editor runs against | git-ignored; contains the probe script |

---

## 5. Data flows

Transition/recompile tools cross a domain reload, so each is **verified by outcome, not by its command response** —
a reload can swallow the response, and "don't trust the return, verify the effect" is the ethos.

### 5.1 Play-mode flow
1. **Precondition** — `get_editor_state` reports `isPlaying:false`; truncate the probe file.
2. **`play_game`** → enters play mode → reload → (server reconnects; harness retries) → verify **both**: probe has
   an `enteredPlayMode` event **and** `get_editor_state.isPlaying == true`. A mismatch is the false-success.
3. **`pause_game`** → probe `pausedChanged:true` **and** `get_editor_state` paused (probe covers it if the field is
   absent).
4. **`stop_game`** → exits play mode → reload → reconnect → probe `exitedPlayMode` **and**
   `get_editor_state.isPlaying == false`.
5. **`finally`** — force edit mode if still playing; clear the probe file.

*(No frame-step: the catalog has no such tool.)*

### 5.2 Script CRUD + recompile flow
1. **`create_script`** (`E2EScratch.cs`, a known **`MonoBehaviour`** class — so step 2's add-component read-back is
   valid) → verify filesystem: `.cs` + `.meta` exist; read-back: `read_script` returns it.
2. **`refresh_assets`** → compile → reload → reconnect → verify **both**: independent **compile signal**
   (`read-editor-log.mjs`: a compile episode ran with **no `error CS`**) **and** read-back (`get_compilation_state`
   reports success; and adding a component of the new type via the bridge **succeeds** — proof it compiled *and*
   loaded, not just that a file was written).
3. **`update_script` with a deliberate compile error** → `refresh_assets` → the harness **expects** `error CS` in
   the log **and** `get_compilation_state` to reflect failure (the failure path — proves the recompile ran and the
   tool wrote the bad code) → then restore valid + recompile clean.
4. **`delete_script`** → filesystem: gone; read-back: `list_scripts` no longer lists it; final clean recompile.
5. **`finally`** — delete every scratch script, recompile, assert the project is error-free so the next run starts
   from a clean warm state.

---

## 6. Error handling & the shared-editor discipline

- **Reconnect timeout** — if a tool call keeps failing past `E2E_TIMEOUT` after a transition, fail that test with
  the `Editor.log` tail attached; never hang.
- **Crash / wedge** — if the editor process has exited (crash) or the bridge stays dead past the timeout,
  crash-recovery relaunches **once** and fails that test.
- **`finally` on every test (the linchpin).** Because it is one shared editor, a test that fails mid-way must still
  run cleanup (exit play mode, delete scratch, restore a clean compile) — otherwise a stuck play-mode or a broken
  script poisons every subsequent test.
- **Expected vs unexpected compile errors** are distinguished per-test; an unexpected `error CS` fails the test.

---

## 7. Validating the harness itself

A test harness is worthless if it cannot fail. v1 includes:

- **Negative controls** — feed the verifiers a false premise and confirm the harness reports **failure**, not a
  pass: assert file-existence for a path nothing wrote, and read-back a script that was never created (`read_script`
  must report not-found). Proves the checks can actually fail — a harness that always passes is worthless.
- **Probe liveness** — a startup assertion that the probe file is being written, so a silent probe failure never
  reads as "no events = pass."
- **Reconnect stability** — repeat the play-mode flow several times in one session to confirm the retry-across-reload
  path is not flaky.

---

## 8. CI-later seam

Everything environment-specific is env-driven: `UNITY_PATH`, `E2E_HOST_PROJECT`, `E2E_TIMEOUT` — no hardcoded
paths. The identical suite runs locally today and later on a **self-hosted / headed CI runner** (real display +
warm `Library`) with zero code change — just env + a runner label. A new `test:e2e:live` npm script; `npm test` and
the floor-matrix are **untouched**. It is explicitly **not** wired into GameCI (headless batch cannot drive
play-mode).

---

## 9. Deferred / out of scope (follow-on, YAGNI)

The harness is built to absorb these; they are follow-on tests, not v1:

- The rest of the untestable set: `refresh_assets`-only edge cases, packages (`add`/`remove`), `run_tests`
  (**with its `giconv`-crash special handling** — a disposable editor / run last / expect-crash-and-reboot),
  `quit_editor` (last — ends the run), `handshake` (out-of-band connect payload), `list_scripts` breadth.
- Standing up the self-hosted CI runner (the seam exists; the runner is separate).
- Automating the one-time host provisioning (a documented manual `-quit` warm-up for v1).

---

## 10. Risks & open questions

- **First-boot cost** — mitigated by the persistent warm `Library` + a minimal host; still a real one-time cost,
  documented as a provisioning step.
- **Transition response race** — `play_game`/`refresh_assets` may lose their command response to the reload; handled
  by verify-by-outcome + retry, never by trusting the response.
- **`get_editor_state` field coverage** — confirm at plan time whether it exposes `isPaused`; if not, the probe is
  the sole pause signal (acceptable — the probe is independent).
- **Probe reliability across reloads** — the probe re-subscribes on every domain load via `[InitializeOnLoad]`;
  covered by the probe-liveness self-check.
