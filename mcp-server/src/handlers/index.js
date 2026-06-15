/**
 * Central export and registration for the MCP tool handlers.
 *
 * v0.5.0 (ADR 0006): the advertised MCP surface is just the three generic meta-tools. Every editor
 * command is reached via `call_unity_tool` after on-demand discovery with `list_unity_tools`; the few
 * Node-logic tools (execute_menu_item / create_script / analyze_screenshot) are dispatched inside
 * call_unity_tool (see core/nodeLogicTools.js) and are deliberately NOT registered as MCP tools.
 */

export { BaseToolHandler } from './base/BaseToolHandler.js';
export { ListUnityInstancesToolHandler } from './instances/ListUnityInstancesToolHandler.js';
export { ListUnityToolsToolHandler } from './instances/ListUnityToolsToolHandler.js';
export { CallUnityToolToolHandler } from './instances/CallUnityToolToolHandler.js';

import { ListUnityInstancesToolHandler } from './instances/ListUnityInstancesToolHandler.js';
import { ListUnityToolsToolHandler } from './instances/ListUnityToolsToolHandler.js';
import { CallUnityToolToolHandler } from './instances/CallUnityToolToolHandler.js';

// The MCP tool surface — the three generic meta-tools (ADR 0006).
const HANDLER_CLASSES = [
  ListUnityInstancesToolHandler,
  ListUnityToolsToolHandler,
  CallUnityToolToolHandler,
];

/**
 * Creates the MCP tool handlers, wired to the connection manager. There is no default/active
 * connection — every meta-tool resolves a connection by EXPLICIT instance via the manager (ADR 0006).
 * @param {import('../core/unityConnectionManager.js').UnityConnectionManager} manager
 * @returns {Map<string, BaseToolHandler>}
 */
export function createHandlers(manager) {
  const handlers = new Map();
  for (const HandlerClass of HANDLER_CLASSES) {
    const handler = new HandlerClass(manager);
    handlers.set(handler.name, handler);
  }
  return handlers;
}
