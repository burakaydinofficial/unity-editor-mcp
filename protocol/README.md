# Unity Editor MCP — Communication Protocol

This directory is the **communication contract** shared by the two halves of the
bridge: the Node MCP server (`mcp-server/`) and the Unity editor package
(`unity-editor-mcp/`). It is a deliberately small, **versioned sub-project** with
its own version line (`VERSION`). It changes infrequently, and a change here is
expected to be satisfied by **both** halves before either is released.

The bridge has three major parts, not two:

1. **`protocol/`** — this module: the command catalog, the wire/result contract,
   the version line, and the drift gate that keeps the two halves honest.
2. **`unity-editor-mcp/`** — the Unity editor domain engine (does the real work).
3. **`mcp-server/`** — the MCP adapter + transport client (speaks MCP to the
   AI client over stdio, the protocol to Unity over TCP).

Why this exists: the two halves were historically two independently hand-edited
dispatch tables that merely shared string keys, so they drifted (tools with no
editor handler, editor commands no client calls). The catalog makes the command
surface a single declared thing, and `check-drift` fails CI when either half
disagrees with it.

## Layout

| Path | Role |
| --- | --- |
| `VERSION` | The protocol semver. The single source for the version number. |
| `catalog/commands.json` | **Canonical** command catalog: every command, its sides, params, and derived result schema. |
| `catalog/commands.schema.json` | JSON Schema for the catalog file itself. |
| `schemas/envelope.schema.json` | Target request/response wire envelope. |
| `schemas/error-codes.json` | Machine error-code vocabulary (I3). |
| `scripts/check-drift.mjs` | The gate: both halves must match the catalog. Run in CI. |
| `scripts/bootstrap-catalog.mjs` | One-time seed/re-baseline of the catalog from current code. |
| `scripts/lib/sources.mjs` | Read-only extractors for each half's command surface. |

## Wire protocol

Transport is **TCP on localhost**, framed as a **4-byte big-endian length prefix
followed by a UTF-8 JSON payload**, in both directions. Messages are correlated
by an `id`.

- **Request:** `{ "id": "...", "type": "<command>", "params": { ... } }`
- **Success:** `{ "id": "...", "status": "success", "result": { ... } }`
- **Error (target):** `{ "id": "...", "status": "error", "code": "<CODE>", "error": "<message>", "remediation": "...", "details": { ... } }`

See `schemas/envelope.schema.json`. Error codes live in `schemas/error-codes.json`.

## The command catalog & the drift gate

`catalog/commands.json` is authoritative. Each entry declares:

- `name` — the `verb_noun` command type (identical on both halves).
- `sides` — which halves must implement it (`server`, `editor`, or both).
- `params` — JSON Schema for the parameters (the server-side input contract,
  extracted mechanically from the live handler definitions).
- `result` — JSON Schema for the success payload, derived best-effort from the editor
  handler returns (`resultSchemaSource: derived-from-handlers-v1`); not yet response-validated.
- `internal`, `destructive`, `category`, `description` — metadata.

Run the gate:

```bash
cd protocol
npm run check      # node scripts/check-drift.mjs
```

It fails (exit 1) on **new** drift: a catalog `side` with no implementation, or
an implemented command missing from the catalog. Pre-existing gaps are baselined
in `catalog.knownGaps` and reported as warnings, so the gate enforces
"no new drift" on the existing codebase. When a baselined gap is fixed, remove it
from `knownGaps` and the gate enforces it from then on.

### Adding or changing a command

1. Edit `catalog/commands.json` first (the contract is the source of truth).
2. Implement/adjust the editor handler + dispatch case in `unity-editor-mcp/`.
3. Implement/adjust the MCP tool handler in `mcp-server/`.
4. `npm run check` must pass.

(Re-running `bootstrap-catalog.mjs` re-seeds from current code; it is for the
initial bootstrap or a deliberate re-baseline, not routine edits.)

## Versioning

Two **independent** version axes — do not conflate them:

- **Protocol version** (`VERSION`, this module) — the wire/catalog contract.
  Changes infrequently. This is what a handshake should negotiate.
- **Release version** (npm package + Unity package) — ships every release,
  lockstepped between the two halves.

Semver meaning on the wire:

- **major** — breaking: remove/rename a command, change/remove a param or result
  field, tighten a constraint, change error semantics. Peers must share a major.
- **minor** — additive/back-compatible: new command, new optional param, new
  error code. Unknown additions are ignored by older peers.
- **patch** — no wire change (descriptions, docs).

Mixed-install handling (the failure these projects die on): each half embeds the
protocol version it was built against; the handshake compares them and returns
`PROTOCOL_VERSION_MISMATCH` on a major mismatch (telling the user which half to
update) rather than hanging. Minor skew within a major is tolerated by the newer
side restricting itself to the lower minor's surface. *(Handshake not yet
implemented — see Roadmap.)*

## Known deviations (current code vs. this contract)

This is tracked honestly so the gap list is the work list:

1. **Errors transported as success.** The editor dispatcher wraps every handler
   result — including `{ error: ... }` — in `SuccessResult`, so domain failures
   arrive with `status:"success"`. The error envelope above is the target; the
   dispatcher must stop laundering errors into successes.
2. **`get_component_types`** is a registered MCP tool with no editor dispatch
   case (baselined in `knownGaps`): it returns `UNKNOWN_COMMAND` at runtime.
3. **No handshake / capability negotiation.** `ping` returns only a pong; the
   connection carries no editor version, project path, or per-tool availability,
   so version/project mismatches are undetectable.
4. **Result schemas are derived, not yet enforced** — `result` is populated best-effort
   from handler returns, but responses are not validated against it.
5. **Fixed port (6400), no project-derived addressing** — concurrent editors are
   not independently addressable without manual configuration.

## Roadmap (specified here, enforced later)

The verified structural backlog (error contract, dispatch generation, lifecycle, …)
lives in `docs/quality-roadmap.md`. Protocol-specific next steps:

- **Result schemas:** validate responses against the derived `result` schemas and
  refine the low-confidence ones (e.g. `get_component_types` once it has a handler).
- **Conformance vectors:** golden request/response fixtures the protocol ships,
  run against both halves (the real "fail both releases" gate).
- **Codegen:** generate the C# command registry/constants and (optionally) C#
  request models from the catalog, so a missing handler is a compile error; an
  optional TypeSpec authoring layer can emit this same JSON Schema.
- **Handshake/capability layer:** exchange protocol version + editor identity +
  per-tool availability on connect; enforce `PROTOCOL_VERSION_MISMATCH` /
  `PROJECT_PATH_MISMATCH` / `UNSUPPORTED_ON_EDITOR_VERSION`.
- **Error contract:** land the discriminated success|error result type on both
  sides and retire the legacy response shapes.
