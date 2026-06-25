# ADR 0002 — Unity-side layering: a dotnet-testable Core

Status: Accepted — fully implemented in v0.4.0 (Core `CommandDispatcher` is the sole dispatch front; legacy `ProcessCommand` switch retired) · Date: 2026-06

## Context

The Unity-side code (`unity-editor-mcp/Editor/`) mixed everything into one
assembly: TCP transport, framing, the command dispatch switch, the response
contract, and the actual `UnityEditor` calls — all behind `[InitializeOnLoad]`.
Because every file could reference `UnityEditor`, none of it could be compiled or
tested without a Unity editor (and a license). That left the highest-risk logic —
framing, dispatch, and the error/result contract — verifiable only by hand,
in-editor, which is also why a floor-breaking change (the unguarded PrefabStage
namespace) reached `main` uncaught.

Observed in the sibling projects: they run **C# in CI via `dotnet test`**, but only
for code that does not depend on Unity (their standalone LSP). The Unity editor
package itself is never compiled in their CI.

## Decision

Split the Unity side into two assemblies along the Unity-dependency seam:

1. **`UnityEditorMCP.Core`** — Unity-INDEPENDENT (`noEngineReferences: true`,
   netstandard2.0 / C# 8). Owns the transport framing, the command/result models,
   the dispatch registry, the protocol/error contract, and a logging seam
   (`IMcpLogger`). References only `Newtonsoft.Json`.
2. **`UnityEditorMCP.Editor`** — Unity-COUPLED. Owns the `[InitializeOnLoad]`
   bootstrap, the main-thread pump (`EditorApplication.update` → `Drain`), the
   domain-reload hooks, the handlers that call `UnityEditor`, and **all the
   `#if UNITY_*` version guards**. Implements `IMcpLogger` over `Debug.Log`.

The seam is the dispatcher + a queue: Core's transport enqueues on a background
thread; the Unity layer decides *when* to drain (main thread). Handlers are
registered by the Unity layer into Core; Core never touches `UnityEditor`.

**Verification:** the Core source is compiled two ways from one set of files —
the Unity asmdef compiles it for the editor, and a plain `.csproj`
(`dotnet/UnityEditorMCP.Core.Tests`) `Compile`-includes the same files so it is
verified by `dotnet test` in CI and locally, at `LangVersion 8` (so anything that
would not build on the 2020.3 floor fails fast). No DLL artifact, no build step
for contributors.

## Consequences

- The framing, dispatch, and **discriminated success|error result contract** are
  now unit-tested without Unity. The "errors laundered as success" defect is fixed
  *by construction* in Core (`HandlerOutcome` has no shape that serializes an error
  as success) and is covered by tests.
- Version-divergent Unity APIs are confined to the thin Unity layer, so Core is
  trivially floor-stable and `noEngineReferences` *enforces* it (Core fails to
  compile if it ever references an engine API).
- The protocol's future conformance vectors can run against Core via `dotnet test`
  — the cheap half of "fail the release" — leaving only the handlers' actual Unity
  calls + the bootstrap/pump/reload plumbing for in-editor verification.
- Cost: an incremental refactor of the Unity side. The proof-of-concept (framing +
  result + dispatcher + 16 tests) is landed; migrating the existing transport,
  handler dispatch, and handlers onto Core follows.

## Status

- Done (all `dotnet test`-verified, 107 tests, + CI lane `csharp-core.yml`):
  - `UnityEditorMCP.Core` framing, command/result models, dispatcher, logger seam.
  - `CommandQueue` (main-thread marshalling seam) and `ProtocolCompatibility`
    (version negotiation).
  - `TcpTransport` — the transport, exercised over a real loopback socket.
  - `Handshake` — protocol version + editor identity + availability payload, with
    protocol- and project-path-mismatch checks (the B3/C3 seed).
  - `McpBridge` — composes transport + parse + queue + dispatcher + framed reply;
    a full command round-trip is tested over a real loopback socket.
  - Catalog → C# codegen: `CommandCatalog.g.cs` (editor command list + protocol
    version) generated from the catalog and drift-gated; `CatalogConformance`
    checks registered handlers against it (editor-side analog of the Node gate).
- Done (step 2 — landed in v0.4.0): the bootstrap
  (`Editor/Core/UnityEditorMCP.cs`) constructs the bridge, registers all handlers
  on the `HandlerOutcome` rail (`BuildDispatcher`), pumps `Drain()` from
  `EditorApplication.update`, sends the `Handshake` on connect, and runs the
  startup `CatalogConformance` check. The legacy `ProcessCommand` switch was fully
  retired (Core's `CommandDispatcher` is now the sole dispatch front), and the
  error-laundering deviation is closed on the wire via `ResponseClassifier`.
