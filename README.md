# Unity Editor MCP

[![CI](https://github.com/burakaydinofficial/unity-editor-mcp/actions/workflows/test-coverage.yml/badge.svg)](https://github.com/burakaydinofficial/unity-editor-mcp/actions/workflows/test-coverage.yml)
[![floor-matrix (Unity 2020.3 · 2021.3 · 2022.3)](https://github.com/burakaydinofficial/unity-editor-mcp/actions/workflows/floor-matrix.yml/badge.svg?event=push)](https://github.com/burakaydinofficial/unity-editor-mcp/actions/workflows/floor-matrix.yml)
[![codecov](https://codecov.io/gh/burakaydinofficial/unity-editor-mcp/branch/main/graph/badge.svg)](https://codecov.io/gh/burakaydinofficial/unity-editor-mcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![npm version](https://img.shields.io/npm/v/@burakaydinofficial/unity-editor-mcp)](https://www.npmjs.com/package/@burakaydinofficial/unity-editor-mcp)

> ⚠️ **This project is in beta (0.x) and under heavy development.** Features and APIs may change. Use at your own discretion.

Unity Editor MCP (Model Context Protocol) enables AI assistants like Claude and Cursor to interact directly with the Unity Editor, allowing for AI-assisted game development and automation. This is a fork focused on being **the deep, floor-true bridge for older Unity projects** — Unity **2020.3 LTS and newer is the tested floor**, with both branches of every version-divergent API kept under guards down to 2019.4 (see [`COMPATIBILITY.md`](COMPATIBILITY.md)).

## 🚀 Key Features

- **🎮 GameObject & Component control** — create/modify/delete GameObjects, **world or local** transforms, full component add/remove/**reorder** with `RequireComponent` awareness
- **🧬 Deep serialization** — read/write *any* serialized property through `SerializedObject` (private `[SerializeField]` included), structural array/list edits, `[SerializeReference]`, `AnimationCurve`, `Gradient` — Inspector-correct (single Undo group + compare-and-swap)
- **🎭 Prefab & asset workflow** — prefab-mode editing, variants, unpack, ScriptableObjects, materials, import settings, dependency analysis
- **🩹 Legacy-project repair** — detect and remove **missing-script** MonoBehaviours (a deleted/moved `.cs` leaves a dangling component)
- **🔍 Scene analysis & search** — find by name/tag/layer/component, scene statistics, reference tracing (all paged/capped for large scenes)
- **🧠 Code intelligence** — always-on syntactic symbol search + file outline; a capability-gated **Roslyn** sidecar upgrades to semantic resolve / type-members / implementations
- **🖼️ Visual capture** — screenshot Game/Scene View **and render an arbitrary world camera**, returned as real MCP **image content** the agent can see
- **🏃 Play mode & tests** — drive play mode, run EditMode/PlayMode tests, read results
- **🖱️ UI automation** — click / set / inspect uGUI elements (Undo-tracked)
- **🛡️ Safety rails** — a confirm-gate on irreversible commands, a project-folder path sandbox, and a local mutation audit log
- **🔌 Version-agnostic surface** — one server works with **any Unity 2020.3+ editor** (guarded to 2019.4) and several editors at once; the client learns each editor's real tools at runtime

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
`call_unity_tool`. Everything below is the **editor capability catalog** (**98 commands across 18 categories**):
the agent discovers each connected editor's real tools — with schemas, learned at runtime — via
`list_unity_tools`, then invokes them by name via `call_unity_tool`.

> **Why a generic surface (v0.5.0 — [ADR 0006](docs/adr/0006-no-default-instance-on-demand-discovery.md)):**
> one server works with **any Unity version and several editors at once**, the client carries 3 tool
> definitions instead of ~98, and **every call names its target editor explicitly** (a project path or
> port — there is no default instance, so an agent can never act on the wrong project). The catalog below
> documents what each editor exposes; those commands are reached through `call_unity_tool`, not advertised
> as individual MCP tools.
>
> **Discover the shape, then trim it.** `list_unity_tools(instance, name: "<tool>")` returns a tool's full
> parameter schema **and** its result-field hints (the response shape — v0.5.0). Every call also accepts an
> optional `fields` parameter — an array of dot-paths (e.g. `["count","objects.name","state.isPlaying"]`) —
> that trims the response to just those fields, GraphQL-style (array elements are transparent; omit for the
> full result).

<!-- The list below is generated from protocol/catalog/commands.json (the contract source of truth). -->

### Instance Management — the 3 advertised meta-tools (3)
- **`list_unity_instances`** — List the Unity editor instances currently running and discoverable (project path, Unity version, port).
- **`list_unity_tools`** — List the tools a connected Unity editor actually supports, with their schemas (learned from the editor at runtime).
- **`call_unity_tool`** — Invoke any tool a connected Unity editor supports, by name (discover names + schemas with list_unity_tools).

### System & Core (7)
- **`ping`** — Test connection to the Unity Editor.
- **`read_logs`** — Read Unity console logs.
- **`refresh_assets`** — Trigger Unity to refresh assets and check for compilation.
- **`get_editor_info`** — Editor environment: Unity version, platform, project path, active build target, product/company name, play/compile state.
- **`get_audit_log`** — Read the local mutation audit log (H5) — recent dispatched commands as `{t, type, target, ok}`.
- **`clear_audit_log`** — Clear the local mutation audit log (H5). *(confirm-gated)*
- **`quit_editor`** — Quit the Unity editor (intended for CI/automation). *(confirm-gated)*

### GameObject Management (6)
- **`create_gameobject`** — Create a GameObject (primitive/empty) with transform, tag, and layer.
- **`find_gameobject`** — Find GameObjects by name, tag, or layer (paged via `limit`).
- **`modify_gameobject`** — Modify a GameObject's name/active/parent and transform, in **world or local** space (`space`).
- **`delete_gameobject`** — Delete GameObject(s), with optional child handling. *(confirm-gated)*
- **`get_hierarchy`** — Get the scene hierarchy (capped via `maxNodes`).
- **`remove_missing_scripts`** — Remove missing-script MonoBehaviours from the active scene (all, or specific paths). *(confirm-gated)*

### Component System (6)
- **`add_component`** — Add a component to a GameObject with initial property values.
- **`remove_component`** — Remove a component (refuses when another component `[RequireComponent]`s it).
- **`modify_component`** — Modify component properties (dot-notation for nested).
- **`list_components`** — List a GameObject's components with type info and removability.
- **`get_component_types`** — Discover addable component types, filterable by category.
- **`reorder_component`** — Reorder a component among its siblings (order affects execution/serialization).

### Scene Analysis (6)
- **`get_gameobject_details`** — Deep inspection of a GameObject: components, values, hierarchy.
- **`analyze_scene_contents`** — Scene statistics, composition, and performance metrics.
- **`get_component_values`** — All properties and values of a specific component.
- **`find_by_component`** — Find GameObjects by component type, scope-filtered (paged).
- **`get_object_references`** — References to/from a GameObject (hierarchy + assets).
- **`find_missing_scripts`** — Find GameObjects with missing-script MonoBehaviours — the legacy-project staple.

### Serialization — deep property access (4)
- **`inspect_serialized_object`** — Discover a target's serialized property tree (path, type, values, array sizes, `[SerializeReference]` types).
- **`set_serialized_properties`** — Write serialized properties via `SerializedObject` (private `[SerializeField]` included) — one Undo group + apply.
- **`modify_serialized_array`** — Structurally mutate array/list properties (resize/insert/remove/move/clear) with a size compare-and-swap.
- **`save_assets`** — Persist all dirty assets to disk (`AssetDatabase.SaveAssets`).

### Scene Management (6)
- **`create_scene`** — Create a new scene (build-settings integration, auto-load).
- **`load_scene`** — Load a scene (Single or Additive).
- **`save_scene`** — Save the current scene (with Save As).
- **`list_scenes`** — List project scenes (filter + build-settings info).
- **`get_scene_info`** — Detailed scene info including GameObject counts.
- **`manage_build_settings`** — Manage the build scene list (`list`/`add`/`remove`/`move`/`set_enabled`/`clear`; `exists` flags dangling build paths).

### Asset & Prefab Management (15)
- **`create_prefab`** — Create a prefab from a GameObject or from scratch.
- **`modify_prefab`** — Modify an existing prefab's properties (and instances).
- **`instantiate_prefab`** — Instantiate a prefab in the scene.
- **`open_prefab` / `exit_prefab_mode` / `save_prefab`** — Prefab-mode editing lifecycle (open, save/apply, exit).
- **`create_prefab_variant`** — Create a prefab variant of a base prefab.
- **`unpack_prefab`** — Unpack a prefab instance (`regular` outermost, or `complete`).
- **`manage_prefab_overrides`** — Inspect + granularly apply/revert prefab-instance overrides (`list`, apply/revert one property, apply/revert all) — vs `save_prefab`'s all-or-nothing.
- **`create_scriptable_object`** — Create a ScriptableObject of a named type and save it as an asset.
- **`create_material` / `modify_material`** — Create/modify materials (shader + properties).
- **`manage_asset_database`** — Asset DB ops: find, info, folders, move, copy, **delete (confirm-gated)**, refresh, save.
- **`manage_asset_import_settings`** — Get/modify import settings, apply presets, reimport, **per-platform texture overrides** (`get_platform`/`set_platform`).
- **`analyze_asset_dependencies`** — Dependencies, dependents, circular deps, unused assets, size impact (dependency lists **paged** via `limit`/`offset`).

### Script Management (6)
- **`create_script`** — Create a new C# script (templates + namespace).
- **`read_script`** — Read a script file's contents.
- **`update_script`** — Update a script (content replacement + validation). *(confirm-gated)*
- **`delete_script`** — Delete a script (dependency check + confirm). *(confirm-gated)*
- **`list_scripts`** — List project scripts with metadata.
- **`validate_script`** — Validate script syntax / compatibility.

### Code Intelligence — semantic (Roslyn-gated) (8)
- **`get_symbols`** — Outline a C# file: types, methods, properties with line ranges.
- **`find_symbol`** — Find symbol declarations by exact name across `Assets` scripts.
- **`find_references`** — Textual references to an identifier (comments/strings excluded; upgrades to semantic when the sidecar is ready).
- **`get_symbol_body`** — Source text of a named symbol within a C# file.
- **`resolve_symbol`** — *(Roslyn)* Resolve an identifier to declaring type(s)/member(s) via compiled assemblies.
- **`get_type_members`** — *(Roslyn)* Members of a named type with signatures and visibility.
- **`find_implementations`** — *(Roslyn)* Subtypes/implementors via `TypeCache`.
- **`export_roslyn_model`** — Export the `CompilationPipeline` project model (sources, references, defines).

### Compilation (3)
- **`get_compilation_state`** — Current compilation state, errors, and warnings.
- **`start_compilation_monitoring` / `stop_compilation_monitoring`** — Real-time compile error monitoring.

### Play Mode (4)
- **`play_game` / `pause_game` / `stop_game`** — Enter/pause/exit play mode (transitions are async — poll state to confirm).
- **`get_editor_state`** — Editor state: play mode, pause, compilation.

### UI Automation (5)
- **`find_ui_elements`** — Locate uGUI elements with filtering.
- **`click_ui_element`** — Simulate clicking buttons/toggles (Undo-tracked).
- **`get_ui_element_state`** — UI element state and interaction capabilities.
- **`set_ui_element_value`** — Set values for sliders/input fields/toggles (Undo-tracked).
- **`simulate_ui_input`** — Execute complex UI interaction sequences.

### Visual Capture (2)
- **`capture_screenshot`** — Capture **Game View / Scene View / a specific world camera**; returns viewable MCP **image content**.
- **`analyze_screenshot`** — Analyze screenshot content (UI elements, colors, basic image analysis).

### Editor Operations — tags, layers, selection, windows, packages, settings (9)
- **`get_editor_info` / `get_project_settings`** — Read editor environment and curated project settings.
- **`set_project_setting`** — Write a curated project setting (PlayerSettings). *(confirm-gated)*
- **`list_packages` / `manage_packages`** — List installed UPM packages; add/remove a package. *(manage is confirm-gated)*
- **`manage_tags` / `manage_layers`** — Manage project tags / layers (add, remove, list).
- **`manage_selection`** — Manage editor selection (get, set, clear).
- **`manage_windows`** — Manage editor windows (list, focus, get state).
- **`manage_tools`** — Manage editor tools/plugins (list, activate, deactivate).

### Menu (2)
- **`execute_menu_item`** — Execute an editor menu item (errors when the item didn't run, rather than reporting a false success).
- **`invoke_static_method`** — Invoke a static method by type + name with JSON args. **Default-deny** (arbitrary code execution) — allow-list via `UNITY_MCP_INVOKE_ALLOW` or `ProjectSettings/UnityEditorMcpInvokePolicy.json`.

### Console (2)
- **`clear_console`** — Clear the editor console.
- **`enhanced_read_logs`** — Read console logs with advanced search/filtering.

### Test Runner (4)
- **`run_tests`** — Run EditMode/PlayMode tests (all, or filtered by name/class/category/assembly).
- **`get_test_results`** — Results of a run (summary + optional per-test detail, filterable by status).
- **`list_tests`** — List available tests without running them.
- **`cancel_tests`** — Cancel a test run in progress.

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
