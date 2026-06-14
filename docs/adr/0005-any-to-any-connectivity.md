# ADR 0005 — Any-to-any MCP ↔ Unity connectivity

Status: Proposed (v0.3.0 strong requirement; design recorded, not yet implemented) · Date: 2026-06

## Context

Today the topology is **1:1 per session**. `server.js` creates a single shared
`UnityConnection` resolved to one editor by `resolveUnityPort` (`UNITY_PORT` → registry
descriptor for `UNITY_PROJECT_PATH` → derived port → 6400). Every handler shares that one
socket. `list_unity_instances` (ADR 0004, v0.2.0) lets a session *see* every editor, but it
can only *act* on the one it connected to — demonstrated live: three editors (2020.3 / 2021.3 /
2022.3) discoverable, only 2020.3 reachable.

**v0.3.0 strong requirement: any-to-any.** Any MCP server can drive any Unity instance, and a
single MCP server can drive several editors concurrently. This is the legacy-dev ICP made real
— 2020.3 + 2021.3 + 2022.3 open at once, one agent session orchestrating across all of them
(e.g. run a test suite on the 2020.3 and the 2022.3 project in one conversation).

## Decision

A full mesh is two independent halves, one on each side of the bridge:

1. **One MCP server → many editors (Node side — the new work).** Replace the single shared
   `UnityConnection` with a **connection manager**: a map of instance-key → `UnityConnection`,
   opened **lazily** when a call first targets that instance, each carrying its own handshake
   (ADR 0004 per-instance schema cache), heartbeat, reconnect/backoff, and 1 MB framing. The
   generic `call_unity_tool({ instance, tool, params })` resolves the target from the registry
   and routes to that instance's connection; an omitted `instance` means the active/default
   target (`set_active_unity_instance`). Connections are pruned when their editor leaves the
   registry. `server.js` stops owning one `UnityConnection` and owns the manager instead.

2. **Many MCP servers → one editor (editor side — already supported).** `Core/TcpTransport`
   already runs a continuous accept loop (`AcceptLoopAsync`), handling each client on its own
   `Task` with a **per-client responder queue** (`HandleClientAsync`'s `outbound` + `Respond`
   closure). So N MCP servers can connect to one editor concurrently *today*. The v0.3.0 work
   here is **validation, not redesign**: confirm the host command queue
   (`UnityEditorMCP.cs` `ProcessCommandQueue`) carries each queued command's responder so
   replies route to the originating client under concurrent load, and that no per-connection
   state is shared across clients.

## Consequences

- **The hard half is already done.** The editor accepts concurrent clients, so any-to-any is
  predominantly a Node-side connection-manager addition — not a protocol or editor redesign.
- **It composes with ADR 0004.** The connection manager is the natural home for the per-instance
  schema caches and health that the generic surface needs; generic dispatch + any-to-any ship as
  one v0.3.0 unit (the generic call is the *surface*, the manager is the *transport* under it).
- **Fan-out across versions in one session** — an agent can act on 2020.3 and 2022.3 in the same
  conversation. No competitor (single-version, single-connection) offers this.
- **Cost:** N live sockets per server, bounded by editors actually targeted (lazy open, not all
  discovered); each needs independent reconnect/heartbeat. The single-active-connection path
  remains the common case and the default.
- **Protocol unaffected:** same framing, same handshake, same per-instance manifest — only the
  *multiplicity* changes. The drift gate and wire contract are untouched.
- **New verification owed:** concurrent-client correctness of the editor command queue +
  responder routing (a `dotnet` `TcpTransport`-level test with a fake host, plus a multi-client
  EditMode check), and Node connection-manager unit tests (routing, lazy open, prune, per-instance
  reconnect).

## Sequencing

v0.3.0, alongside the generic interface (ADR 0004). **v0.2.0 stays single-active-connection +
typed** (`list_unity_instances` is the read-only "see them all" affordance; this ADR is the "act
on any of them" follow-through).
