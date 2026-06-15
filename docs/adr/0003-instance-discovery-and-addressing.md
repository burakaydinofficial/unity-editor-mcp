# ADR 0003 — Instance discovery and addressing

Status: Accepted — implemented and verified live (three LTS editors coexisting on per-project derived ports, discovered through the registry and driven via the bridge) · Date: 2026-06

## Context

The bridge listened on a hardcoded `6400`. With several editors open, the first
one wins and the rest die with `AddressAlreadyInUse`; a server connecting to 6400
cannot know *which* project it reached (the wrong-editor trap). N servers × N
editors needs deterministic, discoverable pairing.

A "well-known coordinator" design was considered (instances register with whoever
holds 6400; ping-pong liveness; takeover when the holder dies). That is a service
registry + leader election — with the registry-of-record being a *Unity editor*,
the least reliable process in the system (domain reloads, recompiles). Rejected:
election races and split-brain for a localhost dev tool.

## Decision

Three layers, each independently overridable:

1. **Derived default port** — `EndpointAddressing.DerivePort(projectPath)` maps
   the project path into `[6400, 7424)` via **FNV-1a over UTF-16 code units**
   (stable across processes and languages; `string.GetHashCode` is neither).
   `UNITY_MCP_PORT` overrides on the editor side; `UNITY_PORT` on the server side.
2. **Filesystem instance registry (authoritative)** — each editor publishes
   `<fnv1a-of-project-path>.json` (`{schemaVersion, projectPath, port, pid,
   unityVersion, protocolVersion, startedAt, lastHeartbeat}`) to a per-user dir:
   `UNITY_MCP_REGISTRY_DIR` || `%LOCALAPPDATA%` | `~/Library/Application Support`
   | `$XDG_DATA_HOME`/`~/.local/share`, + `unity-editor-mcp/instances`. Heartbeat
   every 60 s; descriptors stale after 300 s; removed on quit; reapable. The
   filesystem is the registry — always up, no coordinator, no election. (Same
   pattern as Jupyter's runtime connection files and LSP discovery.)
3. **Ephemeral fallback** — if the derived/configured port is taken, the editor
   binds port 0 (OS-assigned) and publishes the actual port. Collisions stop
   being fatal; the registry makes the port knowable anyway.

Server resolution order (`mcp-server/src/core/discovery.js`, mirrored from C#):
`UNITY_PORT` (explicit) → fresh registry descriptor for `UNITY_PROJECT_PATH` →
derived port for that path → legacy 6400. The `Handshake` project-path check
(`PROJECT_PATH_MISMATCH`) backstops wrong-editor connections.

Cross-language parity is pinned by tests on both sides: canonical FNV-1a vectors
(`EndpointAddressingTests.cs` ↔ `discovery.test.js`), identical directory rules,
filename format, and staleness window; the Node tests parse a .NET
`o`-format-dated descriptor.

## Consequences

- N editors coexist (distinct derived ports or ephemeral fallback) and are
  enumerable with their project, version, and real port — the many-to-many
  becomes per-project one-to-one pairs.
- Discovery is best-effort: registry failure degrades to explicit port config.
- The server resolves the port at startup; re-resolving on reconnect (editor
  restarted onto an ephemeral port) is a known follow-up, as is reaping stale
  descriptors from the server side and surfacing `list instances` as a tool.
