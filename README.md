# Unity Editor MCP

[![CI](https://github.com/burakaydinofficial/unity-editor-mcp/actions/workflows/test-coverage.yml/badge.svg)](https://github.com/burakaydinofficial/unity-editor-mcp/actions/workflows/test-coverage.yml)
[![codecov](https://codecov.io/gh/burakaydinofficial/unity-editor-mcp/branch/main/graph/badge.svg)](https://codecov.io/gh/burakaydinofficial/unity-editor-mcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![npm version](https://img.shields.io/npm/v/@burakaydinofficial/unity-editor-mcp)](https://www.npmjs.com/package/@burakaydinofficial/unity-editor-mcp)

> ⚠️ **This project is in beta and under heavy development.** Features and APIs may change. Use at your own discretion.

Unity Editor MCP (Model Context Protocol) enables AI assistants like Claude and Cursor to interact directly with the Unity Editor, allowing for AI-assisted game development and automation.

## 🚀 Key Features

- **🎮 GameObject Management**: Create primitives, modify transforms, manage hierarchy, and delete objects
- **🔧 Component System**: Add, remove, modify, and list components on GameObjects with full property control
- **🎭 Prefab Workflow**: Complete prefab mode editing - open, modify, save, and exit with override management
- **🔍 Smart Search**: Find GameObjects by name, tag, layer, or component type with exact/partial matching
- **📊 Scene Analysis**: Analyze scene composition, component statistics, and prefab connections
- **🎯 Component Inspection**: Get component values, find objects by component, trace references between objects
- **🎬 Scene Control**: Create, load, save scenes, manage build settings, and work with multiple scenes
- **🏃 Play Mode Testing**: Start, pause, and stop play mode, check editor state and compilation status
- **🖼️ Screenshot Capture**: Take screenshots of Game View or Scene View with analysis capabilities
- **🎨 Asset Management**: Create and modify prefabs, materials, scripts with comprehensive property control
- **🖱️ UI Automation**: Interact with Unity UI elements programmatically for testing and automation
- **📝 Console Integration**: Read Unity console logs filtered by type with enhanced debugging features
- **🔄 Editor Operations**: Refresh assets, execute menu items, and trigger recompilation


## 🚀 Quick Start

### Prerequisites

- ✅ Unity 2020.3 LTS or newer (the tested floor; 2019.4 is a guarded best-effort target — see `COMPATIBILITY.md`)
- ✅ Node.js 18.0.0 or newer  
- ✅ Claude Desktop or Cursor

### Installation

#### 📦 Step 1: Install Unity Package

In Unity:

1. Open **Window → Package Manager**
2. Click **"+"** → **"Add package from git URL..."**
3. Paste: `https://github.com/burakaydinofficial/unity-editor-mcp.git?path=unity-editor-mcp`
4. Click **Add**

> ✨ Unity automatically starts the editor-side bridge on a **per-project port**
> (derived from the project path, range 6400–7423) and publishes it to a local
> discovery registry, so the MCP server finds it automatically — no fixed port to set.

#### ⚙️ Step 2: Configure Your MCP Client

**For Claude Desktop:**

Add to your config file:
- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`  
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`

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

**For Cursor:**

Add the same configuration to Cursor's MCP settings

#### ✅ Step 3: Verify Connection

1. **Restart your MCP client** (Claude Desktop or Cursor)
2. Check Unity Console for: `[Unity Editor MCP] Client connected`
3. You're ready to go! 🎮

## Available Tools

The MCP server advertises a **3-tool generic surface** — `list_unity_instances`, `list_unity_tools`, and
`call_unity_tool`. Everything below is the **editor capability catalog** (~76 commands across 13
categories): the agent discovers each connected editor's real tools — with schemas, learned at runtime —
via `list_unity_tools`, then invokes them by name via `call_unity_tool`.

> **Why a generic surface (v0.5.0 — [ADR 0006](docs/adr/0006-no-default-instance-on-demand-discovery.md)):**
> one server works with **any Unity version and several editors at once**, the client carries 3 tool
> definitions instead of ~80, and **every call names its target editor explicitly** (a project path or
> port — there is no default instance, so an agent can never act on the wrong project). The catalog below
> documents what each editor exposes; those commands are reached through `call_unity_tool`, not advertised
> as individual MCP tools.
>
> **Discover the shape, then trim it.** `list_unity_tools(instance, name: "<tool>")` returns a tool's full
> parameter schema **and** its result-field hints (the response shape — v0.5.0). Every call also accepts an
> optional `fields` parameter — an array of dot-paths (e.g. `["count","objects.name","state.isPlaying"]`) —
> that trims the response to just those fields, GraphQL-style (array elements are transparent; omit for the
> full result).

### System & Core Tools (3 tools)
- **`ping`** - Test connection to Unity Editor and verify server status
- **`read_logs`** - Read Unity console logs with filtering by type (Log, Warning, Error, etc.)
- **`refresh_assets`** - Refresh Unity assets and trigger recompilation

### Instance Management (3 tools)
- **`list_unity_instances`** - List the Unity editors currently running and discoverable (project, version, port); works with no editor connected
- **`list_unity_tools`** - List the tools a connected editor actually supports, with schemas learned at runtime; pass `name` for one tool's full param schema **and result-field hints** (the version-agnostic surface)
- **`call_unity_tool`** - Invoke any tool a connected editor supports by name, validated against its advertised schema; routes to the named instance (required — no default)

### GameObject Management (5 tools)
- **`create_gameobject`** - Create GameObjects with primitives, transforms, tags, and layers
- **`find_gameobject`** - Find GameObjects by name, tag, layer with pattern matching
- **`modify_gameobject`** - Modify GameObject properties (transform, name, active state, parent, etc.)
- **`delete_gameobject`** - Delete single or multiple GameObjects with optional child handling
- **`get_hierarchy`** - Get complete scene hierarchy with components and depth control

### Component System (5 tools)
- **`add_component`** - Add Unity components to GameObjects with initial property values
- **`remove_component`** - Remove components from GameObjects with safety checks (prevents Transform removal)
- **`modify_component`** - Modify component properties with support for nested properties using dot notation
- **`list_components`** - List all components on a GameObject with type information and removability status
- **`get_component_types`** - Discover available component types with filtering by category and addability

### Scene Management (5 tools)
- **`create_scene`** - Create new scenes with build settings integration and auto-loading
- **`load_scene`** - Load existing scenes in Single or Additive mode
- **`save_scene`** - Save current scene with Save As functionality
- **`list_scenes`** - List all scenes in project with filtering and build settings info
- **`get_scene_info`** - Get detailed scene information including GameObject counts

### Scene Analysis (5 tools)
- **`get_gameobject_details`** - Deep inspection of GameObjects with component details and hierarchy
- **`analyze_scene_contents`** - Comprehensive scene statistics, composition, and performance metrics
- **`get_component_values`** - Get all properties and values of specific components with metadata
- **`find_by_component`** - Find GameObjects by component type with scope filtering (scene/prefabs/all)
- **`get_object_references`** - Analyze references between objects including hierarchy and asset connections

### Asset Management (11 tools)
- **`create_prefab`** - Create prefabs from GameObjects or empty templates with overwrite options
- **`modify_prefab`** - Modify existing prefabs with property changes and instance updates
- **`instantiate_prefab`** - Instantiate prefabs in scenes with transform and parenting options
- **`open_prefab`** - Open prefabs in Unity's prefab mode for detailed editing with focus and isolation
- **`exit_prefab_mode`** - Exit prefab mode with optional save/discard changes
- **`save_prefab`** - Save prefab changes in prefab mode or apply instance overrides to prefab assets
- **`create_material`** - Create new materials with shader assignment and property configuration
- **`modify_material`** - Modify existing materials with shader changes and property updates
- **`manage_asset_import_settings`** - Manage Unity asset import settings (get, modify, apply presets, reimport)
- **`manage_asset_database`** - Manage Unity Asset Database operations (find, info, create folders, move, copy, delete, refresh)
- **`analyze_asset_dependencies`** - Analyze Unity asset dependencies (get dependencies, dependents, circular deps, unused assets, size impact)

### Script Management (6 tools)
- **`create_script`** - Create new C# scripts with templates and namespace management
- **`read_script`** - Read script file contents with syntax highlighting information
- **`update_script`** - Modify existing scripts with content replacement and validation
- **`delete_script`** - Delete script files with dependency checking and confirmation
- **`list_scripts`** - List all scripts in project with filtering and metadata
- **`validate_script`** - Validate script syntax and check for compilation errors

### Code Intelligence (4 tools)
- **`get_symbols`** - Outline a C# file's types/methods/properties (with line ranges)
- **`find_symbol`** - Find a symbol by name across the project's Assets scripts (optional kind filter)
- **`find_references`** - Find textual (syntactic) references to an identifier across Assets scripts
- **`get_symbol_body`** - Extract a named symbol's source from a C# file

### Play Mode Controls (4 tools)
- **`play_game`** - Start Unity play mode for testing and interaction
- **`pause_game`** - Pause or resume Unity play mode
- **`stop_game`** - Stop Unity play mode and return to edit mode
- **`get_editor_state`** - Get current Unity editor state (play mode, pause, compilation status)

### UI Automation (5 tools)
- **`find_ui_elements`** - Locate UI elements in scene hierarchy with filtering
- **`click_ui_element`** - Simulate clicking on UI elements (buttons, toggles, etc.)
- **`get_ui_element_state`** - Get detailed UI element state and interaction capabilities
- **`set_ui_element_value`** - Set values for UI input elements (sliders, input fields, etc.)
- **`simulate_ui_input`** - Execute complex UI interaction sequences

### Editor Operations (11 tools)
- **`execute_menu_item`** - Execute Unity menu items programmatically with safety checks
- **`clear_console`** - Clear Unity console logs with optional filtering
- **`enhanced_read_logs`** - Advanced log reading with search, filtering, and export capabilities
- **`capture_screenshot`** - Take screenshots of Game View or Scene View with custom resolution and encoding
- **`analyze_screenshot`** - Analyze screenshot content with basic image analysis capabilities
- **`get_editor_info`** - Read editor/project environment info (Unity version, platform, build target, paths, play/compile state)
- **`get_project_settings`** - Read curated project settings (product/company name, version, color space, screen size, scripting backend, define symbols)
- **`list_packages`** - List installed UPM packages from the manifest + lock files (direct deps + full resolved set with source)
- **`set_project_setting`** - Write a curated project setting (productName, companyName, bundleVersion, defaultScreenWidth/Height, runInBackground, colorSpace, scriptingDefineSymbols)
- **`manage_packages`** - Add or remove a UPM package (asynchronous; verify with list_packages)
- **`quit_editor`** - Quit the Unity editor (deferred so the response flushes first)

### Editor Control & Automation (8 tools)
- **`manage_tags`** - Manage Unity project tags (add, remove, list)
- **`manage_layers`** - Manage Unity project layers (add, remove, list, convert index/name)
- **`manage_selection`** - Manage Unity Editor selection (get, set, clear, get details)
- **`manage_windows`** - Manage Unity Editor windows (list, focus, get state)
- **`manage_tools`** - Manage Unity Editor tools and plugins (list, activate, deactivate, refresh)
- **`start_compilation_monitoring`** - Start monitoring Unity compilation with real-time error detection
- **`stop_compilation_monitoring`** - Stop compilation monitoring and get final status
- **`get_compilation_state`** - Get current Unity compilation state and errors

### Test Runner (4 tools)
- **`run_tests`** - Run Unity EditMode/PlayMode tests (all, or filtered by name/class/category/assembly)
- **`get_test_results`** - Get results of a test run (summary plus optional per-test details, filterable by status)
- **`list_tests`** - List available tests without running them
- **`cancel_tests`** - Cancel a test run in progress


## Troubleshooting

### Unity TCP Listener Issues

The bridge uses a **per-project derived port** (range 6400–7423), published to a local
discovery registry — there is no single fixed port:
1. Ensure the Unity Editor is running with the package installed.
2. Set `UNITY_PROJECT_PATH` so the server resolves the right editor from the registry,
   or set `UNITY_PORT` to pin a specific port explicitly.
3. If a port seems stuck, close stray Unity instances and restart the editor.

### Connection Failed

1. Ensure Unity Editor is running with the package installed
2. Check the Unity console for error messages
3. Verify the Node.js server is running
4. Check your MCP client configuration path is absolute

### Node.js Server Won't Start

1. Ensure you have Node.js 18+ installed: `node --version`
2. Run `npm install` in the mcp-server directory
3. Check for any error messages in the console

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines.

## License

MIT License - see [LICENSE](LICENSE) for details.
