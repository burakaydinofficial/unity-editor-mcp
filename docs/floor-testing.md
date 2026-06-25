# Floor compatibility testing

The fork's headline claim — **works on old Unity LTS, not just Unity 6** — is only credible
if the C# editor side actually *compiles and passes its EditMode tests* on each supported
version. This doc is how that is proven. (`COMPATIBILITY.md` lists the guarded APIs; this is
the verification that the guards are correct on every floor.)

Supported matrix: **2019.4, 2020.3, 2021.3, 2022.3** (LTS) — all in the automated floor-matrix CI
(cold compile + EditMode green on each). 2019.4 is the declared floor (C# 7.3). Unity 6000.x is
API-guarded but its CI host is still pending.

## A. Local matrix (works today — the v1 proof)

You have the four LTS editors installed and the host projects under the git-ignored
`unity-test-projects/<version>/` (wired to the package via `file:` refs + `testables`).
Before tagging a release, run the EditMode suite on each:

1. Open `unity-test-projects/2019.4` in Unity 2019.4.x, run **Window → General → Test Runner
   → EditMode → Run All**. Expect green.
2. Repeat for `2020.3`, `2021.3`, and `2022.3`.
3. Record the result (version + pass count) in the release notes.

This is the **minimum bar for v1**: a reproducible, documented, all-green floor run.
It is manual, but it is real proof — unlike `compat-lint` (a heuristic that flags unguarded
floor-divergent APIs but does not compile anything).

> Tip: the dogfood bridge can drive **one** editor at a time (`run_tests` EditMode). To cover
> all four from an agent loop, point the bridge at each editor in turn.

## B. Automated CI matrix (live)

`.github/workflows/floor-matrix.yml` runs a [GameCI](https://game.ci) EditMode matrix over the
floor LTS versions (2019.4.41f2 / 2020.3.49f1 / 2021.3.45f2 / 2022.3.62f2 — each has a GameCI Linux
image). Both one-time setup tasks are now **DONE**:

1. **Unity license (Personal).** Unity **deprecated** offline/manual `.alf`→`.ulf` activation for
   Personal — `-createManualActivationFile` errors with "access token unavailable" in batch mode, so
   the only path is the Hub-generated license file. Repo secrets are set: `UNITY_LICENSE` (contents of
   `C:\ProgramData\Unity\Unity_lic.ulf`, from Unity Hub → Preferences → Licenses → "Get a free personal
   license"), plus `UNITY_EMAIL` + `UNITY_PASSWORD` — GameCI's `unity-test-runner@v4` activates the
   Personal seat in-container from all three together (the `.ulf` is not used standalone).
2. **Committed host projects.** One minimal host per version under `ci/unity-host-<version>/`:
   `Packages/manifest.json` (refs the package via a relative `file:` path + a `testables` entry;
   `newtonsoft-json` + `test-framework` resolve transitively from the package) and
   `ProjectSettings/ProjectVersion.txt` pinned to that editor. `Library/`/`Temp/` are gitignored (cached).

The workflow fires on **push to the working branch** (a branch-scoped `push` trigger — `workflow_dispatch`
alone won't work until the workflow reaches the default branch, a GitHub limitation). After the first green
run, switch to gating releases/PRs and drop the branch trigger. It still skips on forks (the repo guard).

## What "green" must cover

- The `Editor` assembly **compiles** on each version (the real risk — `#if UNITY_*` guards
  with both branches; see `COMPATIBILITY.md`).
- The NUnit **EditMode tests** pass (~295, incl. the aftermath suite).
- `dotnet test` on the Unity-independent `Core` (framing, dispatch, the wire classifier) —
  already in CI, version-independent, no editor needed.
