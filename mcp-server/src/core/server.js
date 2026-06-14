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
import { filterListedTools } from './toolExposure.js';
import { fileURLToPath } from 'node:url';

/**
 * Converts a handler result ({ status:'success', result } | { status:'error', error,
 * code, details }) into an MCP tool response. Shared by the live request handler and the
 * createServer test helper so both speak the same MCP shape (errors carry isError:true).
 */
function toMcpResponse(result, name) {
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
  const responseText = (result.result === undefined || result.result === null)
    ? JSON.stringify({ status: 'success', message: 'Operation completed successfully but no details were returned', tool: name }, null, 2)
    : JSON.stringify(result.result, null, 2);
  return { content: [{ type: 'text', text: responseText }] };
}

// The generic instance meta-tools are the canonical v0.3.0 surface (ADR 0004): by default tools/list
// advertises only them, so a client isn't born carrying ~70 definitions it may never use. The full
// typed catalog stays reachable via call_unity_tool, and is re-advertised with UNITY_MCP_TYPED_TOOLS=true.
const TYPED_TOOLS_DEFAULT = false;

/**
 * Wires an MCP Server's request handlers over a handler map + connection manager. Shared by main()
 * and createServer so the live server and the test server behave identically.
 */
function registerRequestHandlers(server, handlers) {
  server.setRequestHandler(ListToolsRequestSchema, async () => {
    const all = Array.from(handlers.values()).map((handler) => handler.getDefinition());
    return { tools: filterListedTools(all, process.env, TYPED_TOOLS_DEFAULT) };
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
    // The connection manager + the active/default connection (ADR 0005). The manager lets the
    // instance meta-tools route to any editor; the active connection serves the typed tools.
    const manager = new UnityConnectionManager();
    const unityConnection = manager.getActiveConnection();
    const handlers = createHandlers(unityConnection, manager);

    const server = new Server(
      { name: config.server.name, version: config.server.version },
      { capabilities: { tools: {}, resources: {}, prompts: {} } },
    );
    registerRequestHandlers(server, handlers);

    // Connection lifecycle logging. The connect-time handshake (protocol/project check + manifest
    // caching onto editorInfo) is wired by the manager for every connection it manages.
    unityConnection.on('connected', () => logger.info('Unity connection established'));
    unityConnection.on('disconnected', () => logger.info('Unity connection lost'));
    unityConnection.on('error', (error) => logger.error('Unity connection error:', error.message));

    const transport = new StdioServerTransport();
    await server.connect(transport);
    logger.info('MCP server started successfully');

    // Attempt to connect to Unity (retries automatically on failure).
    try {
      await unityConnection.connect();
    } catch (error) {
      logger.error('Initial Unity connection failed:', error.message);
      logger.info('Unity connection will retry automatically');
    }

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
 * Builds a server + handler map for tests (no transport, no Unity connection). Returns the active
 * connection + manager so tests can drive/inspect them.
 */
export async function createServer(customConfig = config) {
  const testManager = new UnityConnectionManager();
  const testUnityConnection = testManager.getActiveConnection();
  const testHandlers = createHandlers(testUnityConnection, testManager);

  const testServer = new Server(
    { name: customConfig.server.name, version: customConfig.server.version },
    { capabilities: { tools: {}, resources: {}, prompts: {} } },
  );
  registerRequestHandlers(testServer, testHandlers);

  return {
    server: testServer,
    unityConnection: testUnityConnection,
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
