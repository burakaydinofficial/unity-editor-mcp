// One-time bootstrap: seed the canonical command catalog from the *current*
// implementation of both halves ("describe what exists" — protocol v1).
//
// After this seed is committed, catalog/commands.json becomes AUTHORITATIVE:
// it is hand-curated (or, later, emitted from a TypeSpec source) and both
// halves are verified against it by check-drift.mjs. Re-running this script
// regenerates the seed and is only intended for the initial bootstrap or a
// deliberate re-baseline.

import { writeFile } from 'node:fs/promises';
import { getServerTools, getEditorCommands, loadCatalog, PATHS } from './lib/sources.mjs';
import { readFile } from 'node:fs/promises';

const VERSION = (await readFile(new URL('../VERSION', import.meta.url), 'utf8')).trim();

// Preserve curated content from an existing catalog across a re-baseline:
// derived result schemas (anything with a real `type`) must survive re-running
// bootstrap, since they are not recoverable from the source code.
const preserved = new Map();
const preservedSides = new Map();
const preservedCategory = new Map();
let preservedSource;
try {
  const existing = await loadCatalog();
  preservedSource = existing.resultSchemaSource;
  for (const c of existing.commands ?? []) {
    // "Real" schema = anything that isn't the TODO placeholder ({ $comment } only).
    if (c.result && Object.keys(c.result).some((k) => k !== '$comment')) preserved.set(c.name, c.result);
    // Curated sides/category must survive a re-baseline too — otherwise a server-only
    // command (e.g. list_unity_instances, sides:['server']) is silently widened back to
    // ['server','editor'], which then fails the drift gate on the next run. (Audit finding.)
    if (Array.isArray(c.sides) && c.sides.length) preservedSides.set(c.name, c.sides);
    if (typeof c.category === 'string') preservedCategory.set(c.name, c.category);
  }
} catch {
  // No existing catalog (first bootstrap) — nothing to preserve.
}

// Command -> category. Mirrors the HANDLER_CLASSES grouping in
// mcp-server/src/handlers/index.js so the catalog stays human-navigable.
const CATEGORY = {
  ping: 'system', read_logs: 'system', refresh_assets: 'system', clear_logs: 'system',
  create_gameobject: 'gameobject', find_gameobject: 'gameobject', modify_gameobject: 'gameobject',
  delete_gameobject: 'gameobject', get_hierarchy: 'gameobject',
  create_scene: 'scene', load_scene: 'scene', save_scene: 'scene', list_scenes: 'scene', get_scene_info: 'scene',
  get_gameobject_details: 'analysis', analyze_scene_contents: 'analysis', get_component_values: 'analysis',
  find_by_component: 'analysis', get_object_references: 'analysis',
  play_game: 'playmode', pause_game: 'playmode', stop_game: 'playmode', get_editor_state: 'playmode',
  find_ui_elements: 'ui', click_ui_element: 'ui', get_ui_element_state: 'ui',
  set_ui_element_value: 'ui', simulate_ui_input: 'ui',
  create_prefab: 'asset', modify_prefab: 'asset', instantiate_prefab: 'asset', create_material: 'asset',
  modify_material: 'asset', open_prefab: 'asset', exit_prefab_mode: 'asset', save_prefab: 'asset',
  manage_asset_import_settings: 'asset', manage_asset_database: 'asset', analyze_asset_dependencies: 'asset',
  create_script: 'scripting', read_script: 'scripting', update_script: 'scripting',
  delete_script: 'scripting', list_scripts: 'scripting', validate_script: 'scripting',
  execute_menu_item: 'menu',
  clear_console: 'console', enhanced_read_logs: 'console',
  capture_screenshot: 'screenshot', analyze_screenshot: 'screenshot',
  add_component: 'component', remove_component: 'component', modify_component: 'component',
  list_components: 'component', get_component_types: 'component',
  start_compilation_monitoring: 'compilation', stop_compilation_monitoring: 'compilation',
  get_compilation_state: 'compilation',
  manage_tags: 'editor', manage_layers: 'editor', manage_selection: 'editor',
  manage_windows: 'editor', manage_tools: 'editor',
  list_tests: 'test', run_tests: 'test', get_test_results: 'test', cancel_tests: 'test',
  list_unity_instances: 'instances', list_unity_tools: 'instances',
  call_unity_tool: 'instances', set_active_unity_instance: 'instances',
};

// Coarse destructive flags for v1 (per-action granularity is a later refinement).
const DESTRUCTIVE = new Set(['delete_gameobject', 'delete_script']);

const RESULT_TODO = { $comment: 'TODO: derive editor return shape (protocol v1 leaves result schemas unspecified).' };

const serverTools = await getServerTools();
const editorCmds = await getEditorCommands();
const serverNames = new Set(serverTools.map((t) => t.name));

const commands = [];

for (const tool of serverTools) {
  commands.push({
    name: tool.name,
    category: preservedCategory.get(tool.name) ?? CATEGORY[tool.name] ?? 'uncategorized',
    sides: preservedSides.get(tool.name) ?? ['server', 'editor'],
    internal: false,
    destructive: DESTRUCTIVE.has(tool.name),
    description: tool.description,
    params: tool.inputSchema ?? { type: 'object' },
    result: preserved.get(tool.name) ?? RESULT_TODO,
  });
}

// Editor-only commands that exist in the dispatcher but are not exposed as MCP
// tools (internal / legacy). Recorded so the drift gate does not flag them.
for (const name of editorCmds) {
  if (serverNames.has(name)) continue;
  commands.push({
    name,
    category: CATEGORY[name] ?? 'system',
    sides: ['editor'],
    internal: true,
    destructive: DESTRUCTIVE.has(name),
    description: 'Internal editor command (not exposed as an MCP tool).',
    params: { type: 'object' },
    result: preserved.get(name) ?? RESULT_TODO,
  });
}

commands.sort((a, b) => a.category.localeCompare(b.category) || a.name.localeCompare(b.name));

// Baseline pre-existing implementation gaps so the drift gate enforces "no NEW
// drift" without being blocked by known defects. Each entry is a tracked TODO:
// remove it once the missing side is implemented (the gate then enforces it).
const editorSet = new Set(editorCmds);
const knownGaps = [];
for (const tool of serverTools) {
  // Only a tool that is SUPPOSED to have an editor side (curated sides include 'editor',
  // or the default) counts as a gap. A deliberately server-only command is not a gap.
  const sides = preservedSides.get(tool.name) ?? ['server', 'editor'];
  if (sides.includes('editor') && !editorSet.has(tool.name)) {
    knownGaps.push({
      command: tool.name,
      missing: 'editor',
      note:
        'Registered MCP tool with no Unity dispatch case — returns UNKNOWN_COMMAND at runtime. ' +
        'Fix: implement the editor handler and add the case, or remove the tool.',
    });
  }
}

const catalog = {
  $schema: './commands.schema.json',
  protocol: VERSION,
  ...(preservedSource ? { resultSchemaSource: preservedSource } : {}),
  description:
    'Canonical command catalog for the Unity Editor MCP bridge. Authoritative source ' +
    'of the command surface shared by the Node server and the Unity editor package.',
  knownGaps,
  commands,
};

await writeFile(PATHS.catalog, JSON.stringify(catalog, null, 2) + '\n', 'utf8');
console.log(`Wrote ${commands.length} commands to ${PATHS.catalog} (protocol ${VERSION}).`);
