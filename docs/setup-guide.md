# Unity Editor MCP — Setup Guide

This guide walks through installing and running the Unity Editor MCP (Model Context
Protocol) bridge. For the architecture see the [ADRs](adr/) and the root
[README](../README.md); for multi-version floor testing see
[floor-testing.md](floor-testing.md).

## Prerequisites

- Unity **2020.3 LTS** or newer (the bridge is floor-true down to 2020.3)
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
| `UNITY_MCP_TYPED_TOOLS` | `true` re-advertises the full typed catalog as individual MCP tools (default off — only the generic meta-tools are listed) |
| `LOG_LEVEL` | `info` (default) or `debug` |

Editor side: `UNITY_MCP_PORT` overrides the derived port. Server config defaults
live in `mcp-server/src/core/config.js`.

## The tool surface

By default the server exposes a small **generic surface**: `list_unity_instances`,
`list_unity_tools`, `call_unity_tool`, and `set_active_unity_instance`. The agent
discovers each editor's real tools (with schemas, learned at runtime) and invokes
them by name — so one server works with any Unity version and several editors at
once. Set `UNITY_MCP_TYPED_TOOLS=true` to list the ~66 typed tools individually.

## Verifying the connection

1. Start Unity (the bridge starts automatically) and the MCP server.
2. From your client, call **`list_unity_instances`** — every running editor shows
   up with its project, version, port, and which one is the active target.
3. Call **`call_unity_tool`** with `{ "tool": "ping" }` (or set
   `UNITY_MCP_TYPED_TOOLS=true` and call `ping` directly) — you should get a `pong`.

## Testing

Node server (from `mcp-server/`):

```bash
npm test           # unit + integration
npm run test:ci    # the curated CI gate
```

Unity EditMode tests: open the project and run **Window → General → Test Runner →
EditMode → Run All**. See [floor-testing.md](floor-testing.md) for the
multi-version (2020.3 / 2021.3 / 2022.3) floor matrix.

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
