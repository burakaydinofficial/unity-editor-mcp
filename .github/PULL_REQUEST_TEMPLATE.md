## What & why

<!-- Brief description of the change and the motivation. -->

## Compatibility checklist

This fork is floor-true (see [CONTRIBUTING.md](../CONTRIBUTING.md)).

- [ ] Any version-divergent Unity API is behind `#if UNITY_X_Y_OR_NEWER` with **both branches** maintained, and cataloged in `COMPATIBILITY.md`.
- [ ] Unity-side C# stays within **C# 8 / netstandard 2.0** (no UI Toolkit in core paths).
- [ ] Node server stays **pure JS, no native modules**.
- [ ] `node scripts/compat-lint.mjs` passes.

## Contract & tests

- [ ] Command/tool changes edit `protocol/catalog/commands.json` first; `node protocol/scripts/check-drift.mjs` passes.
- [ ] `cd mcp-server && npm run test:ci` passes.
- [ ] `dotnet test dotnet/UnityEditorMCP.Core.Tests/...` passes (if `Core/` touched).
- [ ] EditMode tests added/updated for editor handlers (floor-matrix CI green).
- [ ] Docs updated — README catalog + `docs/tools-reference.md` regenerated if the catalog changed.
