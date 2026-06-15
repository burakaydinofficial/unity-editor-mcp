# Unity Editor MCP Server

MCP (Model Context Protocol) server for Unity Editor integration. Enables AI assistants like Claude and Cursor to interact directly with Unity Editor for automated game development.

## Features

- **Version-agnostic generic surface** - the server learns each connected editor's tools (with schemas, at runtime) and exposes them through three meta-tools, so one server works with **any Unity version** and **several editors at once**
- **~78 editor tools** spanning GameObjects, components, scenes, scene analysis, assets (prefabs/materials/import settings), scripts, code intelligence, play mode, UI automation, the Test Runner, and editor operations
- **Multi-instance routing** - discover every running editor and target any of them by project path or port
- **Pure ESM, zero native modules** - `npx`-friendly; the only runtime dependency is the MCP SDK

## Quick Start

### Using npx (Recommended)

```bash
npx @burakaydinofficial/unity-editor-mcp
```

### Global Installation

```bash
npm install -g @burakaydinofficial/unity-editor-mcp
unity-editor-mcp
```

### Local Installation

```bash
npm install @burakaydinofficial/unity-editor-mcp
npx @burakaydinofficial/unity-editor-mcp
```

## Unity Setup

1. Install the Unity package from: `https://github.com/burakaydinofficial/unity-editor-mcp.git?path=unity-editor-mcp`
2. Open Unity Package Manager → Add package from git URL
3. The package automatically starts a loopback TCP bridge on a **per-project derived port** (range 6400–7423) and publishes it to a local discovery registry, so the server finds it automatically — no fixed port to configure

## MCP Client Configuration

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "unity-editor-mcp": {
      "command": "npx",
      "args": ["@burakaydinofficial/unity-editor-mcp"]
    }
  }
}
```

### Alternative (if globally installed)

```json
{
  "mcpServers": {
    "unity-editor-mcp": {
      "command": "unity-editor-mcp"
    }
  }
}
```

## Tool surface

**The server advertises a small generic surface (v0.5.0 — ADR 0006):**

- **`list_unity_instances`** — list the running, discoverable editors (project, version, port); works even when none is connected
- **`list_unity_tools`** — list the tools a connected editor supports, with schemas learned at runtime
- **`call_unity_tool`** — invoke any of those tools by name (params validated against the editor's advertised schema before the call), routed to the named instance (required as of v0.5.0 — no default)

This is what lets one server drive **any Unity version** and **several editors at once**. The agent discovers each editor's tools on demand via `list_unity_tools` and invokes them by name with `call_unity_tool` — there is no per-tool advertised surface (ADR 0006).

The editor exposes ~78 tools spanning GameObjects, components, scenes, scene analysis, assets (prefabs / materials / import settings), scripts, code intelligence, play mode, UI automation, the Test Runner, and editor operations. The complete, categorized catalog lives in the [project README](https://github.com/burakaydinofficial/unity-editor-mcp#available-tools).

## Requirements

- **Unity**: 2020.3 LTS or newer
- **Node.js**: 18.0.0 or newer
- **MCP Client**: Claude Desktop, Cursor, or compatible client

## Troubleshooting

### Connection Issues
1. Ensure Unity Editor is running with the Unity package installed
2. Check Unity console for connection messages (it logs the port it bound)
3. The bridge is loopback-only (localhost) on a per-project port in 6400–7423 — make sure local TCP connections aren't blocked. Run `list_unity_instances` to confirm the server can see the editor

### Installation Issues
```bash
# Clear npm cache
npm cache clean --force

# Reinstall
npm uninstall -g @burakaydinofficial/unity-editor-mcp
npm install -g @burakaydinofficial/unity-editor-mcp
```

## Repository

Full source code and documentation: https://github.com/burakaydinofficial/unity-editor-mcp

## License

MIT License - see LICENSE file for details.