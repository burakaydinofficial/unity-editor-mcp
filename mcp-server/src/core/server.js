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
  logger.info(`[MCP] Returning success response for: ${name} at ${new Date().toISOString()}`);
  const responseText = (result.result === undefined || result.result === null)
    ? JSON.stringify({ status: 'success', message: 'Operation completed successfully but no details were returned', tool: name }, null, 2)
    : JSON.stringify(result.result, null, 2);
  return { content: [{ type: 'text', text: responseText }] };
}

// Create the connection manager + the active/default connection (ADR 0005). The manager lets the
// instance meta-tools route to any editor; the active connection serves the typed tools and the
// default path and env-resolves its port on each connect.
const manager = new UnityConnectionManager();
const unityConnection = manager.getActiveConnection();

// Create tool handlers (typed handlers use the active connection; meta-tools use the manager).
const handlers = createHandlers(unityConnection, manager);

// Whether the static typed tools are listed when UNITY_MCP_TYPED_TOOLS is unset. Stage 3a keeps
// the v0.2.0 behavior (typed listed by default); Stage 3b flips this to false so the generic
// meta-tools are the canonical surface and typed tools become opt-in (ADR 0004).
const TYPED_TOOLS_DEFAULT = true;

// Create MCP server
const server = new Server(
  {
    name: config.server.name,
    version: config.server.version,
  },
  {
    capabilities: {
      tools: {},
      resources: {},
      prompts: {}
    }
  }
);

// Register MCP protocol handlers
// Note: Do not log here as it breaks MCP protocol initialization

// Handle tool listing
server.setRequestHandler(ListToolsRequestSchema, async () => {
  const all = Array.from(handlers.values()).map(handler => handler.getDefinition());
  return { tools: filterListedTools(all, process.env, TYPED_TOOLS_DEFAULT) };
});

// Handle resources listing
server.setRequestHandler(ListResourcesRequestSchema, async () => {
  logger.debug('[MCP] Received resources/list request');
  // Unity MCP server doesn't provide resources
  return { resources: [] };
});

// Handle prompts listing
server.setRequestHandler(ListPromptsRequestSchema, async () => {
  logger.debug('[MCP] Received prompts/list request');
  // Unity MCP server doesn't provide prompts
  return { prompts: [] };
});

// Handle tool execution
server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;
  const requestTime = Date.now();
  
  logger.info(`[MCP] Received tool call request: ${name} at ${new Date(requestTime).toISOString()}`, { args });
  
  const handler = handlers.get(name);
  if (!handler) {
    logger.error(`[MCP] Tool not found: ${name}`);
    throw new Error(`Tool not found: ${name}`);
  }
  
  try {
    logger.info(`[MCP] Starting handler execution for: ${name} at ${new Date().toISOString()}`);
    const startTime = Date.now();
    
    // Handler returns response in our format
    const result = await handler.handle(args);
    
    const duration = Date.now() - startTime;
    const totalDuration = Date.now() - requestTime;
    logger.info(`[MCP] Handler completed at ${new Date().toISOString()}: ${name}`, { 
      handlerDuration: `${duration}ms`,
      totalDuration: `${totalDuration}ms`,
      status: result.status 
    });
    
    // Convert to MCP format (shared with createServer)
    return toMcpResponse(result, name);
  } catch (error) {
    const errorTime = Date.now();
    logger.error(`[MCP] Handler threw exception at ${new Date(errorTime).toISOString()}: ${name}`, { 
      error: error.message, 
      stack: error.stack,
      duration: `${errorTime - requestTime}ms`
    });
    return {
      isError: true,
      content: [
        {
          type: 'text',
          text: `Error: ${error.message}`
        }
      ]
    };
  }
});

// Connection lifecycle logging. The connect-time handshake (protocol/project check + manifest
// caching onto editorInfo) is wired by the manager for every connection it manages (ADR 0004/0005).
unityConnection.on('connected', () => logger.info('Unity connection established'));
unityConnection.on('disconnected', () => logger.info('Unity connection lost'));
unityConnection.on('error', (error) => logger.error('Unity connection error:', error.message));

// Initialize server
export async function main() {
  try {
    // Create transport - no logging before connection
    const transport = new StdioServerTransport();
    
    // Connect to transport
    await server.connect(transport);
    
    // Now safe to log after connection established
    logger.info('MCP server started successfully');
    
    // Attempt to connect to Unity
    try {
      await unityConnection.connect();
    } catch (error) {
      logger.error('Initial Unity connection failed:', error.message);
      logger.info('Unity connection will retry automatically');
    }
    
    // Handle shutdown
    process.on('SIGINT', async () => {
      logger.info('Shutting down...');
      manager.disconnectAll();
      await server.close();
      process.exit(0);
    });
    
    process.on('SIGTERM', async () => {
      logger.info('Shutting down...');
      manager.disconnectAll();
      await server.close();
      process.exit(0);
    });
    
  } catch (error) {
    console.error('Failed to start server:', error);
    console.error('Stack trace:', error.stack);
    process.exit(1);
  }
}

// Export for testing
export async function createServer(customConfig = config) {
  const testManager = new UnityConnectionManager();
  const testUnityConnection = testManager.getActiveConnection();
  const testHandlers = createHandlers(testUnityConnection, testManager);
  
  const testServer = new Server(
    {
      name: customConfig.server.name,
      version: customConfig.server.version,
    },
    {
      capabilities: {
        tools: {},
        resources: {},
        prompts: {}
      }
    }
  );
  
  // Register handlers for test server
  testServer.setRequestHandler(ListToolsRequestSchema, async () => {
    const all = Array.from(testHandlers.values()).map(handler => handler.getDefinition());
    return { tools: filterListedTools(all, process.env, TYPED_TOOLS_DEFAULT) };
  });
  
  testServer.setRequestHandler(ListResourcesRequestSchema, async () => {
    return { resources: [] };
  });
  
  testServer.setRequestHandler(ListPromptsRequestSchema, async () => {
    return { prompts: [] };
  });
  
  testServer.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: args } = request.params;
    
    const handler = testHandlers.get(name);
    if (!handler) {
      return toMcpResponse({ status: 'error', error: `Tool not found: ${name}`, code: 'TOOL_NOT_FOUND' }, name);
    }

    // Mirror the live handler's exception guard so createServer has true parity
    // (handle() is designed not to throw, but a wrapped transport must never reject).
    try {
      return toMcpResponse(await handler.handle(args), name);
    } catch (error) {
      return { isError: true, content: [{ type: 'text', text: `Error: ${error.message}` }] };
    }
  });
  
  return {
    server: testServer,
    unityConnection: testUnityConnection,
    manager: testManager
  };
}

// Start the server ONLY when run directly (e.g. `node src/core/server.js`), not when
// imported — otherwise importing createServer in tests starts a real stdio server and
// a module-level Unity connection, keeping the process alive (the prior hang root cause).
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error('Fatal error:', error);
    console.error('Stack trace:', error.stack);
    process.exit(1);
  });
}