// Shared source readers for the protocol tooling.
//
// These helpers extract the *de-facto* command surface from each half of the
// bridge so the contract tooling can compare both implementations against the
// canonical catalog. They are intentionally dependency-free and read-only.

import { readFile } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
export const REPO_ROOT = resolve(HERE, '..', '..', '..');

export const PATHS = {
  serverHandlersIndex: resolve(REPO_ROOT, 'mcp-server', 'src', 'handlers', 'index.js'),
  editorCore: resolve(REPO_ROOT, 'unity-editor-mcp', 'Editor', 'Core', 'UnityEditorMCP.cs'),
  catalog: resolve(REPO_ROOT, 'protocol', 'catalog', 'commands.json'),
};

/**
 * Instantiates the Node MCP server's handler registry and returns each tool's
 * MCP definition ({ name, description, inputSchema }). This is the authoritative
 * server-side parameter contract — it is the exact schema advertised to clients.
 */
export async function getServerTools() {
  const stub = {
    isConnected: () => false,
    connect: async () => {},
    sendCommand: async () => ({}),
  };
  const { createHandlers } = await import(pathToFileURL(PATHS.serverHandlersIndex).href);
  const handlers = createHandlers(stub);
  return [...handlers.values()]
    .map((h) => h.getDefinition())
    .sort((a, b) => a.name.localeCompare(b.name));
}

/**
 * Extracts the command-type strings the Unity editor actually handles. Commands
 * are dispatched by two mechanisms during the McpBridge migration (ADR 0002):
 *   1. the legacy ProcessCommand `switch` (`case "..."` labels), and
 *   2. Core's CommandDispatcher, onto which commands are migrated one at a time
 *      (`dispatcher.Register("...", ...)`).
 * The de-facto editor-side surface is the union of both.
 */
export async function getEditorCommands() {
  const src = await readFile(PATHS.editorCore, 'utf8');
  const names = new Set();
  const caseRe = /case\s+"([a-z0-9_]+)"\s*:/g;
  let m;
  while ((m = caseRe.exec(src)) !== null) names.add(m[1]);
  const registerRe = /\.Register\(\s*"([a-z0-9_]+)"/g;
  while ((m = registerRe.exec(src)) !== null) names.add(m[1]);
  return [...names].sort();
}

/** Loads the canonical command catalog. */
export async function loadCatalog() {
  const raw = await readFile(PATHS.catalog, 'utf8');
  return JSON.parse(raw);
}
