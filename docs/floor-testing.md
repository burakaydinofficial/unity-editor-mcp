# Floor compatibility testing

The fork's headline claim — **works on old Unity LTS, not just Unity 6** — is only credible
if the C# editor side actually *compiles and passes its EditMode tests* on each supported
version. This doc is how that is proven. (`COMPATIBILITY.md` lists the guarded APIs; this is
the verification that the guards are correct on every floor.)

Supported matrix (initial): **2020.3, 2021.3, 2022.3** (LTS), plus latest **6000.x**.
Floor of intent: 2019.4 (C# 7.3) — guarded but not yet in the automated matrix.

## A. Local matrix (works today — the v1 proof)

You have the three LTS editors installed and the host projects under the git-ignored
`unity-test-projects/<version>/` (wired to the package via `file:` refs + `testables`).
Before tagging a release, run the EditMode suite on each:

1. Open `unity-test-projects/2020.3` in Unity 2020.3.x, run **Window → General → Test Runner
   → EditMode → Run All**. Expect green.
2. Repeat for `2021.3` and `2022.3`.
3. Record the result (version + pass count) in the release notes.

This is the **minimum bar for v1**: a reproducible, documented, all-green floor run.
It is manual, but it is real proof — unlike `compat-lint` (a heuristic that flags unguarded
floor-divergent APIs but does not compile anything).

> Tip: the dogfood bridge can drive **one** editor at a time (`run_tests` EditMode). To cover
> all three from an agent loop, point the bridge at each editor in turn.

## B. Automated CI matrix (the fast-follow for 1.0.0)

`.github/workflows/floor-matrix.yml` scaffolds a [GameCI](https://game.ci) EditMode matrix.
To make it live, two setup tasks remain (both one-time):

1. **Unity license** — a free Personal license activates in CI. Add repo secrets
   `UNITY_EMAIL`, `UNITY_PASSWORD`, and `UNITY_LICENSE` (the contents of the `.ulf` produced
   by `game-ci/unity-request-activation-file` → manual activation → `.ulf`).
2. **Committed host projects** — CI cannot use the git-ignored `unity-test-projects/`.
   Add a minimal host per version under `ci/unity-host-<version>/` containing only:
   `Packages/manifest.json` (refs the package via a relative `file:` path + a `testables`
   entry for `com.<scope>.unity-editor-mcp`) and `ProjectSettings/ProjectVersion.txt`
   pinned to that editor. Keep them tiny — they exist only to host the package's tests.

Until both are in place the workflow is `workflow_dispatch`-only and skips on forks without
secrets, so it never blocks PRs.

## What "green" must cover

- The `Editor` assembly **compiles** on each version (the real risk — `#if UNITY_*` guards
  with both branches; see `COMPATIBILITY.md`).
- The NUnit **EditMode tests** pass (currently 71).
- `dotnet test` on the Unity-independent `Core` (framing, dispatch, the wire classifier) —
  already in CI, version-independent, no editor needed.
