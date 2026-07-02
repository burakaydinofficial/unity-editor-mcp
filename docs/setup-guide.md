# Unity Editor MCP — Setup Guide

This guide walks through installing and running the Unity Editor MCP (Model Context
Protocol) bridge. For the architecture see the [ADRs](adr/) and the root
[README](../README.md); for multi-version floor testing see
[floor-testing.md](floor-testing.md).

## Prerequisites

- Unity **2019.4 LTS** or newer (the bridge is floor-true down to 2019.4; CI-verified on 2019.4–2022.3)
- Node.js **18+** and npm
- Claude Desktop, Cursor, or another MCP-compatible client

## Installation

### 1. Unity package (the editor bridge)

Install via Unity Package Manager → **Add package from git URL**:

```
https://github.com/burakaydinofficial/unity-editor-mcp.git?path=unity-editor-mcp
```

(Equivalently, add `"com.burakk.unity-editor-mcp"` to `Packages/manifest.json`
pointing at that git URL. For local development you can reference a checkout via a
`file:` path instead.)

On load the package starts a loopback TCP bridge on a **per-project derived port**
(FNV-1a over the project path, range 6400–7423, with an ephemeral fallback if the
derived port is taken) and publishes a descriptor to a per-user discovery registry.
There is **no fixed port to configure** — the server discovers the editor
automatically (ADR 0003).

### 2. MCP server (the adapter)

The published npm package runs straight from `npx` — no clone required:

```bash
npx @burakaydinofficial/unity-editor-mcp
```

For development against this repo:

```bash
cd mcp-server
npm install
npm start          # or: npm run dev  (watch mode)
```

## MCP client configuration

Point your client at the server. For Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "unity-editor-mcp": {
      "command": "npx",
      "args": ["@burakaydinofficial/unity-editor-mcp@latest"]
    }
  }
}
```

Cursor and other clients take the same `command` / `args`.

### Optional environment variables

The server resolves the editor automatically, but you can override:

| Variable | Effect |
| --- | --- |
| `UNITY_PROJECT_PATH` | Resolve the target editor by project path (via the registry / derived port) |
| `UNITY_PORT` | Connect to an explicit port (wins over discovery) |
| `UNITY_HOST` | Host to connect to (default `localhost`) |
| `UNITY_MCP_REGISTRY_DIR` | Override the discovery registry directory |
| `LOG_LEVEL` | `info` (default) or `debug` |

Editor side: `UNITY_MCP_PORT` overrides the derived port. Server config defaults
live in `mcp-server/src/core/config.js`.

## The tool surface

By default the server exposes a small **generic surface**: `list_unity_instances`,
`list_unity_tools`, and `call_unity_tool`. The agent discovers each editor's real
tools (with schemas, learned at runtime) and invokes them by name — so one server
works with any Unity version and several editors at once. Every call names its
target instance explicitly (required as of v0.5.0 — there is no default); pass `name`
to `list_unity_tools` for one tool's full param schema and result-field hints.

## Verifying the connection

1. Start Unity (the bridge starts automatically) and the MCP server.
2. From your client, call **`list_unity_instances`** — every running editor shows
   up with its project, version, and port.
3. Call **`call_unity_tool`** with `{ "instance": "<project path or port from step 2>", "tool": "ping" }`
   (every call names its instance — there is no default) — you should get a `pong`.

## Testing

Node server (from `mcp-server/`):

```bash
npm test           # unit tests (note: the dev glob scripts need Node >= 21; the server itself runs on >= 18)
npm run test:ci    # the curated CI gate (explicit file list — works on any supported Node)
```

Unity EditMode tests: open the project and run **Window → General → Test Runner →
EditMode → Run All**. See [floor-testing.md](floor-testing.md) for the
multi-version (2019.4 / 2020.3 / 2021.3 / 2022.3) floor matrix.

### Live E2E harness (maintainers)

The tools that *cannot* run in an EditMode test — play-mode transitions and script recompiles destroy the test's own
app domain — are covered by a live harness that launches a real **headed** editor on the committed host project
`ci/e2e-host` and drives it through the actual MCP chain (client → server subprocess → editor):

```bash
cd mcp-server
UNITY_PATH="C:/Program Files/Unity/Hub/Editor/<version>/Editor/Unity.exe" npm run test:e2e:live
```

- Flows: `--flow=playmode | scripts | selfcheck | all` (default `all`); `--selfcheck` appends the negative controls
  and a second play-mode pass (reconnect stability). `E2E_KEEP=1` retains the scratch dir (editor log + probe file)
  for debugging; `E2E_HOST_PROJECT` overrides the host; `E2E_TIMEOUT` bounds bridge bring-up (default 180s).
- First run on a new machine provisions the host `Library` (one-time cold import); later runs reuse it warm.
- Every effect is verified by outcome through **two channels**: bridge read-back *and* an independent signal (the
  in-editor probe's JSONL, the filesystem, or the `error CS` log grammar). See ADR 0007 and
  `docs/superpowers/specs/2026-06-28-live-editor-e2e-harness-design.md`.

## Troubleshooting

**Server can't find the editor**
- Confirm Unity is running with the package installed; the console logs the port it bound.
- Run `list_unity_instances` — if the editor isn't listed, pass `includeStale: true` to diagnose.
- The bridge is loopback-only; ensure local TCP connections aren't blocked.

**Connection drops**
- The server reconnects automatically with exponential backoff (capped at 30s).
- A domain reload (recompile) briefly drops the bridge; it reconnects on its own.

**Commands time out**
- The per-command timeout is **30 seconds**. A blocked editor main thread — e.g. a
  modal dialog raised by a running test — stalls command processing until dismissed.

## Architecture

```
┌──────────────┐   stdio (MCP)   ┌──────────────────┐  loopback TCP   ┌───────────────┐
│  MCP client  │◄───────────────►│   Node MCP       │  (derived        │ Unity Editor  │
│  (Claude/…)  │                 │   server         │   per-project    │ (bridge)      │
│              │                 │                  │   port)          │               │
└──────────────┘                 └──────────────────┘                 └───────────────┘
                  discovers the editor via the per-user registry (ADR 0003)
```

The wire protocol is a 4-byte big-endian length prefix + UTF-8 JSON in both
directions (see [protocol/README.md](../protocol/README.md)).

## More

- [README](../README.md) — overview, full tool catalog, install
- [ADRs](adr/) — architecture decisions (protocol, layering, discovery, generic surface, any-to-any)
- [COMPATIBILITY.md](../COMPATIBILITY.md) — the floor policy and guarded APIs
- Open an issue on GitHub for bugs or questions
