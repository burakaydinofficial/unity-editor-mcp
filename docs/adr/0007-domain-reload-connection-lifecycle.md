# ADR 0007 — Domain-reload connection lifecycle and the reconnect strategy

Status: Accepted — pragmatic fast-reconnect implemented (`TcpTransport` clean-close on reload + server keepalive); the out-of-process sidecar is recorded as the future drop-free option · Date: 2026-06

## Context

The MCP↔editor bridge is a TCP connection whose sockets, background threads, and command queue all live in the Unity
editor's managed app domain. A **domain reload** — triggered by a script recompile *or* by entering/exiting play mode
(with Reload Domain on) — tears the entire managed domain down and rebuilds it. The connection therefore **cannot
survive a reload**; it must reconnect afterward. This is inherent: nothing user-owned in managed code outlives a domain
reload — only a **separate process** or **native code** can hold a socket across it.

The live E2E harness (see `docs/superpowers/*/2026-06-28-live-editor-e2e-harness-*`) surfaced a latent bug in that
reconnect. On `beforeAssemblyReload`, `Core/TcpTransport.Stop()` cancelled its `CancellationTokenSource` and stopped the
listener, but never closed the accepted **client** socket. Because `NetworkStream.ReadAsync` ignores the
`CancellationToken` for a *pending* read (Mono/.NET), the client handler stayed blocked and the socket was left
**half-open** — no FIN reached the server. The server only reconnects on a socket `close`/`error`, so it never noticed,
never reconnected, and every command issued after the reload vanished into the dead socket until an eventual OS reset.
This was **latent for every client** (any command promptly after any recompile / play-mode toggle), not just the harness.

## Decision

Ship the **pragmatic fast-reconnect**: make the drop instant and self-healing rather than pursue an (impossible in pure
managed code) drop-free connection.

- **Editor** (`Core/TcpTransport.Stop()`): track accepted clients and **force-close** them. Closing the socket makes the
  blocked `ReadAsync` throw *and* sends the FIN, so the server detects the drop and reconnects in **~1 s**.
- **Server** (`mcp-server/src/core/unityConnection.js`): TCP **keepalive** (10 s) as defense-in-depth for the no-clean-close
  cases (an editor crash rather than a clean reload).
- **Clients** that issue a command across the reload **retry** (the harness does this via `retry.mjs`); the reload window
  is sub-second.

The out-of-process **bridge sidecar** — a separate process that owns the stable server-facing connection and **buffers**
commands across the editor's reloads so the server never sees a drop — is recorded here as the *proper future
architecture* if a truly drop-free interface is ever required. It mirrors the existing Roslyn sidecar
(`dotnet/UnityEditorMCP.Roslyn`). It is **deferred** because no command can *execute* during a reload regardless (the
domain is rebuilding): the sidecar's benefit is seamless buffering vs the pragmatic drop-and-retry — the *same*
end-to-end latency, a UX difference — not worth the added process/IPC/buffering + idempotency complexity now.

## Consequences

- Reload recovery is prompt (~1 s) for **every client**, not just the harness; the play-mode and script/recompile tools
  are now drivable across their reloads (which is what unblocked E2E coverage of them).
- A command sent in the sub-second reload window still fails *once* and must be retried by the client (a sidecar would
  hide this). Acceptable for a localhost dev tool.
- Floor-safe: the change is C# 7.3 / netstandard 2.0 (a `ConcurrentDictionary` of accepted clients); Core `dotnet test`
  exercises the transport (149/149).
- If drop-free is later required, adopt the sidecar — route the core bridge through the same out-of-process front door as
  Roslyn, giving one stable external interface for everything.
