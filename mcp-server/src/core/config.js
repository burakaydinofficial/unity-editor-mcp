/**
 * Configuration for Unity Editor MCP Server
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { resolveUnityPort } from './discovery.js';

// Advertise the real package version to MCP clients (previously hardcoded to
// '0.1.0', which disagreed with package.json and the Unity package version).
const pkg = JSON.parse(
  readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../package.json'), 'utf8')
);

export const config = {
  // Unity connection settings
  unity: {
    host: process.env.UNITY_HOST || 'localhost',
    // NOTE: vestigial in v0.5.0 (ADR 0006) — UnityConnectionManager never reads config.unity.port;
    // every connection is resolved from an EXPLICIT `instance`. Kept for diagnostics / standalone use.
    // (UNITY_PORT wins; else UNITY_PROJECT_PATH via the discovery registry or the derived default; else 6400.)
    port: resolveUnityPort(process.env),
    reconnectDelay: 1000, // Initial reconnect delay in ms
    maxReconnectDelay: 30000, // Maximum reconnect delay
    reconnectBackoffMultiplier: 2,
    commandTimeout: 30000, // Command timeout in ms
  },
  
  // Server settings
  server: {
    name: 'unity-editor-mcp-server',
    version: pkg.version,
    description: 'MCP server for Unity Editor integration',
  },
  
  // Logging settings
  logging: {
    level: process.env.LOG_LEVEL || 'info',
    prefix: '[Unity Editor MCP]',
  }
};

/**
 * Logger utility
 * IMPORTANT: In MCP servers, all stdout output must be JSON-RPC protocol messages.
 * Logging must go to stderr to avoid breaking the protocol.
 */
export const logger = {
  info: (message, ...args) => {
    if (['info', 'debug'].includes(config.logging.level)) {
      console.error(`${config.logging.prefix} ${message}`, ...args);
    }
  },
  
  warn: (message, ...args) => {
    if (['info', 'debug', 'warn'].includes(config.logging.level)) {
      console.error(`${config.logging.prefix} WARN: ${message}`, ...args);
    }
  },
  
  error: (message, ...args) => {
    console.error(`${config.logging.prefix} ERROR: ${message}`, ...args);
  },
  
  debug: (message, ...args) => {
    if (config.logging.level === 'debug') {
      console.error(`${config.logging.prefix} DEBUG: ${message}`, ...args);
    }
  }
};