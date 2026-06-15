# ADR 0004 — Capability-driven tool surface (version-agnostic server)

Status: Accepted, partially superseded by [ADR 0006](0006-no-default-instance-on-demand-discovery.md) — implemented in v0.3.0 (generic meta-tools are the canonical surface; the `UNITY_MCP_TYPED_TOOLS` typed-tools opt-in described below was **removed in v0.5.0**, so the surface is now ONLY the generic meta-tools) · Date: 2026-06

## Context

The MCP tool surface is static and hardcoded. `createHandlers()` instantiates a fixed
`HANDLER_CLASSES` list (67 typed handlers); `server.js`'s `ListToolsRequestSchema` returns
`handlers.values().map(getDefinition)` — the same 67 tools regardless of which editor (if any)
is connected. The catalog (`protocol/catalog/commands.json`, 68 commands incl. internal) is the
contract both halves conform to, enforced by `check-drift.mjs`.

That model fails the fork's core user — the legacy developer running **several Unity versions and
projects at once** (the reason old-LTS support exists; see `unity-mcp-fork-requirements.md`):

1. The list can't reflect a *specific* editor's real capability — tools vary by Unity version and
   installed packages (Input System, Addressables, version-gated APIs).
2. A second editor of a different version opened mid-session isn't represented.
3. **Cold start** (no editor up yet) and the multi-instance case have no clean answer. The obvious
   "generate the list from the connected editor at startup" variant produces an **empty tool list**
   when Unity isn't running and a **stale** one when a second instance appears — both unacceptable.
4. A fixed 67-tool (growing) list is an **always-on context tax** in *every* session the MCP is
   installed in — including ones that never touch Unity — and pushes against client tool-count
   limits as the surface grows.

Alternatives considered and rejected:

- **Instance-tailored typed list + `notifications/tools/list_changed`.** Empty at cold start, stale
  on a second instance, and `list_changed` client support is unreliable (uneven across Claude/
  Cursor; mid-session tool churn confuses agent and user). Dynamism in the *list* is the trap.
- **Static union of all typed tools + per-call routing.** Never empty, but keeps the always-on
  context tax and shows a confusing cross-version superset (tools that don't apply to the editor
  in hand). Better than today, still wrong for the multi-version ICP.

## Decision

**Push all dynamism out of the MCP tool *list* and into tool *results* + call-time routing.** The
list is small, fixed, and connection-independent; what varies (which editors, their versions, what
each supports, where a call goes) is fetched fresh per call, never encoded in list membership.

1. **Generic meta-tools are the canonical surface** — always present, never empty:
   - `list_unity_instances` — live editors with project/version/port. Essentially
     `readInstances(registryDir).filter(isLive)` (`discovery.js`), shaped for the agent.
   - `list_unity_tools({ instance?, category? })` — the *target editor's* real command surface and
     schemas, from its handshake manifest. **Lazy:** names + one-line summaries by default, full
     schema on demand / by category, to bound context.
   - `call_unity_tool({ instance?, tool, params })` — generic dispatch; routes to the active or
     named instance via `unityConnection.sendCommand(tool, params)`.
   - Active-instance selection (`set_active_unity_instance`, or an optional `instance` arg
     defaulting to the `resolveUnityPort` target) so the single-editor case needs no `instance`.

2. **The instance is the source of truth for its own surface.** Extend the handshake to carry full
   **param schemas** — today `Handshake.cs`/`handshake.js` ship `availableCommands: string[]`
   (names only), though the schemas already exist in the catalog / `CommandCatalog.g.cs`. Node
   caches the manifest **per instance** on the connection (`editorInfo`) and re-handshakes on each
   reconnect, clearing the cache first so a domain-reload recompile never serves a stale manifest.
   (A handshake digest to skip re-parsing an unchanged manifest is a possible future optimization,
   not currently implemented.)

3. **Node validates every `call_unity_tool` against the cached schema before the wire.** The MCP
   client cannot gate the inner payload — in the generic envelope `function_params` is an opaque
   `object`, so the model generates it unconstrained and the harness passes it through. Node is the
   **sole gate**, so it must return precise, field-level, correctable errors
   (e.g. `radius is required (number)`). This needs real JSON-Schema validation (pure-JS `ajv`, or a
   minimal subset — no native modules per the Node constraint), not today's required-presence-only
   `BaseToolHandler.validate`.

4. **Typed exposure becomes opt-in.** An env flag (e.g. `UNITY_MCP_TYPED_TOOLS`, optionally scoped
   to categories) re-exposes the catalog as native typed handlers — today's behavior, demoted from
   default to option, for capable setups/clients that prefer native tools or a curated subset.

5. **Catalog role shifts** from "the Node's hardcoded list" to "the union reference + per-call
   validation source." The drift gate becomes one-sided: verify the Unity side's *advertised*
   surface matches the catalog (the Node side no longer hardcodes it).

## Consequences

- **Version-agnostic server.** One published package talks to every Unity, old and new; each call
  is validated against the editor's *own* advertised schema, so per-version/per-package accuracy is
  automatic with zero version logic in Node. Forward-compatible: a newer editor's unknown tools are
  reachable through `call_unity_tool` immediately (typed promotion via `list_changed` is optional
  polish). This is the "longest-supported" mission expressed as architecture.
- **Context economics invert.** Low fixed cost (a handful of meta-tools) + pay-as-you-go discovery.
  A non-Unity session pays ~nothing; a sub-agent carries the tool weight only where the work
  happens, instead of every session being born with 67+ definitions.
- **Multi-instance becomes first-class** — `list_unity_instances` + per-call routing serve the
  legacy ICP (several editors at once), surfacing the capability the registry already supports but
  Node never exposed (closes the `list instances` follow-up noted in ADR 0003).
- **Cost paid:** slightly lower first-try param accuracy (unconstrained generation), recovered by
  the Node validator + clear errors; a discovery round-trip per new instance (amortized across
  calls). The validator's quality is load-bearing — a weak one makes generic dispatch feel flaky.
- **New dependency decision:** a pure-JS JSON-Schema validator, the one departure from the current
  single-runtime-dependency posture (honors "no native modules").
- **Sequencing — concrete release ladder.**
  - **v0.2.0** — single active connection + the static typed surface (hardened base) **plus**
    `list_unity_instances` (read-only, on existing `discovery.js`). Shipped: the multi-editor
    story is *visible* at v0.2.0 even though a session still acts on one editor.
  - **v0.3.0 (strong requirement)** — the generic interface (handshake-schema advertisement,
    generic `call_unity_tool`, the JSON-Schema validator, lazy `list_unity_tools`, typed-exposure
    opt-in, and the flip of generic to default) **together with any-to-any MCP↔Unity
    connectivity (ADR 0005)**. The generic surface is the *what the agent sees*; ADR 0005 is the
    *connection topology underneath it* — they ship as one unit.
