# ADR 0001 — Protocol contract and the three-part architecture

Status: Accepted · Date: 2026-06

## Context

The bridge was built as two parts — a Node MCP server (`mcp-server/`) and a Unity
editor package (`unity-editor-mcp/`) — coupled only by command-type strings that
must be typed identically in three places (the Node tool `name`, the
`sendCommand(type, …)` call, and the C# `switch` case). Nothing enforced that the
two halves agreed. The result was structural drift, not occasional bugs:

- a registered MCP tool with no editor handler (`get_component_types`),
- an editor command no client calls (`clear_logs`),
- and, in the closely-related sibling projects (same lineage, same author),
  119 vs 129 tools with diverged switches — empirical proof that two
  hand-maintained dispatch tables drift apart with no shared contract.

The error contract was also broken by construction: the editor dispatcher wraps
every handler result — including `{ error: … }` — as `SuccessResult`, so domain
failures travel with `status:"success"`. And there is no handshake, so editor
version / project identity / per-tool availability are unknown to the server.

## Decision

**1. Treat the communication protocol as a third major part** — a versioned
sub-project (`protocol/`) that both halves depend on. The architecture is:

1. `protocol/` — the command catalog, wire/result contract, version line, and
   the drift gate.
2. `unity-editor-mcp/` — the Unity editor **domain engine**.
3. `mcp-server/` — the MCP **adapter + transport client**.

**2. Make the command surface a single declared contract.** `protocol/catalog/
commands.json` is authoritative; `check-drift` fails CI when either half diverges
from it. Pre-existing gaps are explicitly baselined (`knownGaps`) so the gate
enforces "no new drift" immediately on the existing codebase.

**3. Version the protocol on its own axis.** `protocol/VERSION` is independent of
the npm / Unity release versions. The protocol changes infrequently; a change
must be satisfied by both halves before either releases. The release versions are
lockstepped separately and the handshake (future) negotiates the *protocol*
version to catch mixed installs.

**4. Brownfield-first.** Protocol v1 *describes what exists* — parameter schemas
are extracted mechanically from the live Node handler definitions; result schemas,
the unified error type, and the handshake/capability layer are specified in the
contract and implemented in later phases (see `protocol/README.md` → Roadmap).

## Why these choices

- **Wire stays JSON over length-prefix.** A binary IDL buys little for a
  localhost editor bridge and costs a Unity managed dependency + debuggability.
- **Canonical = JSON Schema + manifest, generated artifacts committed.** Unity
  compiles whatever C# is on disk; we cannot run a Node codegen at package import,
  so any generated C# must be checked in and verified up-to-date in CI. (An
  optional TypeSpec authoring layer can emit the same JSON Schema later.)
- **Drift gate before codegen.** Locking the boundary and the versioning
  machinery first delivers the anti-drift guarantee now; richer codegen
  (C# registry/constants from the catalog) and conformance vectors follow.

## Consequences

- Adding/changing a command means editing the catalog first, then both halves;
  CI rejects divergence.
- The known defects are now tracked artifacts (`knownGaps`, COMPATIBILITY.md),
  not latent surprises.
- Follow-on work is sequenced: result schemas → unified error type (stop
  laundering errors as success) → handshake/capability layer → codegen +
  conformance vectors. These are refactors of existing behavior, not new
  features.

## Status of related work

- Done: `protocol/` module, drift gate (in CI), catalog seed, PrefabStage floor
  guard (B2), COMPATIBILITY.md (B1), corrected server version advertisement.
- Specified, not yet enforced: unified error envelope, result schemas, handshake,
  codegen, conformance vectors, CI version matrix (A3).
