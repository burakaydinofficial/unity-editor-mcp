#!/usr/bin/env node
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import {
  ListToolsRequestSchema,
  CallToolRequestSchema,
  ListResourcesRequestSchema,
  ListPromptsRequestSchema
} from '@modelcontextprotocol/sdk/types.js';
import { UnityConnectionManager } from './unityConnectionManager.js';
import { createHandlers } from '../handlers/index.js';
import { config, logger } from './config.js';
import { fileURLToPath } from 'node:url';

/**
 * Converts a handler result ({ status:'success', result } | { status:'error', error,
 * code, details }) into an MCP tool response. Shared by the live request handler and the
 * createServer test helper so both speak the same MCP shape (errors carry isError:true).
 */
export function toMcpResponse(result, name) {
  if (result.status === 'error') {
    logger.error(`[MCP] Handler returned error: ${name}`, { error: result.error, code: result.code });
    return {
      isError: true,
      content: [{
        type: 'text',
        text: `Error: ${result.error}\nCode: ${result.code || 'UNKNOWN_ERROR'}${result.details ? '\nDetails: ' + JSON.stringify(result.details, null, 2) : ''}`
      }]
    };
  }
  logger.debug(`[MCP] success response for: ${name}`);
  const payload = result.result;
  // Viewable image content (MCP image block): a capture/render returns { image: { mimeType, data } } (data =
  // base64, no data-URI prefix). The model SEES the image; the text echo carries the rest of the payload with the
  // base64 replaced by a { mimeType, bytes } marker so a multi-MB string isn't duplicated. (G5 — visual capture.)
  const img = (payload && typeof payload === 'object' && !Array.isArray(payload)) ? payload.image : null;
  if (img && typeof img.data === 'string' && img.data.length > 0) {
    const mimeType = img.mimeType || 'image/png';
    const { image, ...rest } = payload;
    return {
      content: [
        { type: 'image', data: img.data, mimeType },
        { type: 'text', text: JSON.stringify({ ...rest, image: { mimeType, bytes: img.data.length } }, null, 2) }
      ]
    };
  }
  const responseText = (payload === undefined || payload === null)
    ? JSON.stringify({ status: 'success', message: 'Operation completed successfully but no details were returned', tool: name }, null, 2)
    : JSON.stringify(payload, null, 2);
  return { content: [{ type: 'text', text: responseText }] };
}

/**
 * Wires an MCP Server's request handlers over a handler map + connection manager. Shared by main()
 * and createServer so the live server and the test server behave identically.
 */
function registerRequestHandlers(server, handlers) {
  // The MCP surface is the 3 generic meta-tools (ADR 0006); every editor command is reached via
  // call_unity_tool after on-demand discovery, so there is nothing else to filter out of the list.
  server.setRequestHandler(ListToolsRequestSchema, async () => {
    const all = Array.from(handlers.values()).map((handler) => handler.getDefinition());
    return { tools: all };
  });
  server.setRequestHandler(ListResourcesRequestSchema, async () => ({ resources: [] }));
  server.setRequestHandler(ListPromptsRequestSchema, async () => ({ prompts: [] }));
  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: args } = request.params;
    logger.debug(`[MCP] tool call: ${name}`, { args });
    const handler = handlers.get(name);
    if (!handler) {
      logger.error(`[MCP] Tool not found: ${name}`);
      return toMcpResponse({ status: 'error', error: `Tool not found: ${name}`, code: 'TOOL_NOT_FOUND' }, name);
    }
    // handle() is designed not to throw, but the transport must never reject — guard anyway.
    try {
      return toMcpResponse(await handler.handle(args), name);
    } catch (error) {
      logger.error(`[MCP] Handler threw: ${name}: ${error.message}`);
      return { isError: true, content: [{ type: 'text', text: `Error: ${error.message}` }] };
    }
  });
}

/**
 * Initialize and run the live server. All live objects (manager, connection, handlers, MCP server)
 * are constructed HERE, not at module top, so importing this module (e.g. createServer in tests) has
 * no side effects. (Audit finding.)
 */
export async function main() {
  try {
    // The connection manager pools a connection per editor instance (ADR 0005). There is NO
    // default/active connection (ADR 0006): every meta-tool resolves a connection by explicit
    // instance, opened lazily on first use — so the server starts without targeting any editor, and
    // a wrong/forgotten instance fails loudly instead of acting on the wrong project.
    const manager = new UnityConnectionManager();
    const handlers = createHandlers(manager);

    const server = new Server(
      { name: config.server.name, version: config.server.version },
      { capabilities: { tools: {}, resources: {}, prompts: {} } },
    );
    registerRequestHandlers(server, handlers);

    const transport = new StdioServerTransport();
    await server.connect(transport);
    logger.info('MCP server started (no editor targeted until a tool names an instance)');

    const shutdown = async () => {
      logger.info('Shutting down...');
      manager.disconnectAll();
      await server.close();
      process.exit(0);
    };
    process.on('SIGINT', shutdown);
    process.on('SIGTERM', shutdown);
  } catch (error) {
    console.error('Failed to start server:', error);
    console.error('Stack trace:', error.stack);
    process.exit(1);
  }
}

/**
 * Builds a server + handler map for tests (no transport, no Unity connection). Returns the server +
 * connection manager so tests can drive/inspect them. There is no default connection (ADR 0006).
 */
export async function createServer(customConfig = config) {
  const testManager = new UnityConnectionManager();
  const testHandlers = createHandlers(testManager);

  const testServer = new Server(
    { name: customConfig.server.name, version: customConfig.server.version },
    { capabilities: { tools: {}, resources: {}, prompts: {} } },
  );
  registerRequestHandlers(testServer, testHandlers);

  return {
    server: testServer,
    manager: testManager
  };
}

// Start the server ONLY when run directly (e.g. `node src/core/server.js`), not when imported —
// otherwise importing createServer in tests would start a real stdio server (the prior hang root cause).
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error('Fatal error:', error);
    console.error('Stack trace:', error.stack);
    process.exit(1);
  });
}
