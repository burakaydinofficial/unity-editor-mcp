import { ExecuteMenuItemToolHandler } from '../handlers/menu/ExecuteMenuItemToolHandler.js';
import { CreateScriptToolHandler } from '../handlers/scripting/CreateScriptToolHandler.js';
import { AnalyzeScreenshotToolHandler } from '../handlers/screenshot/AnalyzeScreenshotToolHandler.js';

/**
 * The few tools that carry genuine Node-side logic and therefore are NOT pure editor passthroughs
 * (ADR 0006; handler audit 2026-06-15 — 76 passthrough / 3 Node-logic):
 *   - execute_menu_item: an input-edge security boundary (Unicode/homograph normalization + blacklist
 *     before the path reaches the editor);
 *   - create_script: generates the full C# source from a template spec Node-side;
 *   - analyze_screenshot: a base64 offline branch + analysis that need not contact the editor.
 *
 * They are NOT advertised as MCP tools. The agent discovers them through `list_unity_tools` and invokes
 * them through `call_unity_tool` like every editor command — but call_unity_tool dispatches them to a
 * Node handler (bound to the resolved instance connection) instead of forwarding raw to the editor.
 * Their agent-facing param schema (the Node handler's) overrides the editor command of the same name.
 * (create_script + analyze_screenshot are flagged to migrate into the editor later.)
 */
export const NODE_LOGIC_TOOLS = {
  execute_menu_item: { handler: ExecuteMenuItemToolHandler, category: 'menu' },
  create_script: { handler: CreateScriptToolHandler, category: 'scripting' },
  analyze_screenshot: { handler: AnalyzeScreenshotToolHandler, category: 'screenshot' },
};

export function isNodeLogicTool(name) {
  return Object.prototype.hasOwnProperty.call(NODE_LOGIC_TOOLS, name);
}

/** The list_unity_tools descriptor for a Node-logic tool (its Node handler's schema is the contract). */
function descriptorFor(name) {
  const { handler: HandlerClass, category } = NODE_LOGIC_TOOLS[name];
  const def = new HandlerClass(null).getDefinition(); // definition only — no connection needed
  return { name: def.name, category, description: def.description, params: def.inputSchema ?? null };
}

/**
 * Overrides the Node-logic tools in an editor manifest surface IN PLACE: where the editor advertises a
 * command of the same name, its entry is replaced by the Node handler's descriptor (the agent-facing
 * contract). It does not append tools the editor doesn't advertise — every real editor build advertises
 * all three, and over-advertising one a given editor lacks would mislead. Pure — returns a new array.
 */
export function mergeNodeLogicSurface(surface) {
  return surface.map((t) => (isNodeLogicTool(t.name) ? descriptorFor(t.name) : t));
}
