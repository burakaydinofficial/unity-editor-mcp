import net from 'net';
import { EventEmitter } from 'events';
import { config, logger } from './config.js';
import { resolveUnityPort, reapStale, defaultRegistryDirectory } from './discovery.js';

/**
 * Detects a handler-level error returned by the Unity editor under a
 * status:"success" envelope. The editor dispatcher currently wraps handler
 * `{ error: ... }` results in SuccessResult, so domain failures would otherwise
 * arrive as successes. The bridge convention is that a top-level string `error`
 * marks a failure and never appears on a success payload, so this is safe to
 * treat as an error at the boundary. (The wire-truth fix — the editor emitting a
 * real ErrorResult — is tracked in protocol/README.md; this keeps MCP clients
 * correct until then, and is also defensive against any future regression.)
 * @param {*} result - the unwrapped payload from a success-status response
 * @returns {boolean}
 */
export function isHandlerLevelError(result) {
  return (
    result !== null &&
    typeof result === 'object' &&
    !Array.isArray(result) &&
    typeof result.error === 'string' &&
    result.success !== true
  );
}

/**
 * Manages TCP connection to Unity Editor
 */
export class UnityConnection extends EventEmitter {
  constructor() {
    super();
    this.socket = null;
    this.connected = false;
    this.reconnectAttempts = 0;
    this.reconnectTimer = null;
    this.commandId = 0;
    this.pendingCommands = new Map();
    this.isDisconnecting = false;
    this.messageBuffer = Buffer.alloc(0);
  }

  /**
   * Resolves the port to (re)connect to. Re-evaluated on every attempt so a
   * restarted editor that moved to a new ephemeral port is picked up via the
   * discovery registry; opportunistically reaps dead descriptors while there
   * (ADR 0003). Explicit UNITY_PORT short-circuits to a fixed port.
   * @param {object} env
   * @returns {number}
   */
  resolveTargetPort(env = process.env) {
    try {
      if (env.UNITY_PROJECT_PATH && !env.UNITY_PORT) {
        try { reapStale(defaultRegistryDirectory(env)); } catch { /* best effort */ }
      }
      return resolveUnityPort(env);
    } catch {
      return config.unity.port;
    }
  }

  /**
   * Connects to Unity Editor
   * @returns {Promise<void>}
   */
  async connect() {
    return new Promise((resolve, reject) => {
      if (this.connected) {
        resolve();
        return;
      }

      // Skip connection in CI/test environments
      if (process.env.NODE_ENV === 'test' || process.env.CI === 'true') {
        logger.info('Skipping Unity connection in test/CI environment');
        reject(new Error('Unity connection disabled in test environment'));
        return;
      }

      const targetPort = this.resolveTargetPort();
      logger.info(`Connecting to Unity at ${config.unity.host}:${targetPort}...`);

      this.socket = new net.Socket();
      let connectionTimeout = null;
      let resolved = false;
      
      // Helper to clean up the connection timeout
      const clearConnectionTimeout = () => {
        if (connectionTimeout) {
          clearTimeout(connectionTimeout);
          connectionTimeout = null;
        }
      };
      
      // Set up event handlers
      this.socket.on('connect', () => {
        logger.info('Connected to Unity Editor');
        this.connected = true;
        this.reconnectAttempts = 0;
        resolved = true;
        clearConnectionTimeout();
        this.emit('connected');
        resolve();
      });

      this.socket.on('data', (data) => {
        this.handleData(data);
      });

      this.socket.on('error', (error) => {
        logger.error('Socket error:', error.message);
        this.emit('error', error);
        
        if (!this.connected && !resolved) {
          resolved = true;
          clearConnectionTimeout();
          // Remove listeners before destroying so the async 'close' event cannot
          // fire scheduleReconnect() after we've already rejected this attempt
          // (socket.destroy() queues 'close' asynchronously). Mirrors the timeout
          // path below.
          this.socket.removeAllListeners();
          this.socket.destroy();
          this.socket = null;
          reject(error);
        }
      });

      this.socket.on('close', () => {
        // Clear the connection timeout when socket closes
        clearConnectionTimeout();
        
        // Check if we're already handling disconnection
        if (this.isDisconnecting || !this.socket) {
          return;
        }
        
        logger.info('Disconnected from Unity Editor');
        this.connected = false;
        this.socket = null;
        
        // Clear message buffer
        this.messageBuffer = Buffer.alloc(0);
        
        // Clear pending commands
        for (const [id, pending] of this.pendingCommands) {
          pending.reject(new Error('Connection closed'));
        }
        this.pendingCommands.clear();
        
        // Emit disconnected event
        this.emit('disconnected');
        
        // Attempt reconnection only if not intentionally disconnecting
        if (!this.isDisconnecting && process.env.DISABLE_AUTO_RECONNECT !== 'true') {
          this.scheduleReconnect();
        }
      });

      // Attempt connection
      this.socket.connect(targetPort, config.unity.host);
      
      // Set timeout for initial connection
      connectionTimeout = setTimeout(() => {
        if (!this.connected && !resolved && this.socket) {
          resolved = true;
          // Remove event listeners before destroying to prevent callbacks after timeout
          this.socket.removeAllListeners();
          this.socket.destroy();
          this.socket = null;
          // The 'close' handler (which normally drains pendingCommands) was just
          // removed, so drain any in-flight commands here. Each reject() clears its
          // own per-command timeout, so no timers leak.
          for (const [, pending] of this.pendingCommands) {
            pending.reject(new Error('Connection timeout'));
          }
          this.pendingCommands.clear();
          reject(new Error('Connection timeout'));
        }
      }, config.unity.commandTimeout);
    });
  }

  /**
   * Disconnects from Unity Editor
   */
  disconnect() {
    this.isDisconnecting = true;
    
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    
    if (this.socket) {
      try {
        // Remove all listeners before destroying to prevent async callbacks
        this.socket.removeAllListeners();
        this.socket.destroy();
      } catch (error) {
        // Ignore errors during cleanup
      }
      this.socket = null;
    }
    
    this.connected = false;
    this.isDisconnecting = false;
  }

  /**
   * Schedules a reconnection attempt
   */
  scheduleReconnect() {
    if (this.reconnectTimer) {
      return;
    }

    const delay = Math.min(
      config.unity.reconnectDelay * Math.pow(config.unity.reconnectBackoffMultiplier, this.reconnectAttempts),
      config.unity.maxReconnectDelay
    );

    logger.info(`Scheduling reconnection in ${delay}ms (attempt ${this.reconnectAttempts + 1})`);

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.reconnectAttempts++;
      this.connect().catch((error) => {
        logger.error('Reconnection failed:', error.message);
      });
    }, delay);
  }

  /**
   * Handles incoming data from Unity
   * @param {Buffer} data
   */
  handleData(data) {
    // Check if this is an unframed Unity debug log
    if (data.length > 0 && !this.messageBuffer.length) {
      const dataStr = data.toString('utf8');
      if (dataStr.startsWith('[Unity Editor MCP]') || dataStr.startsWith('[Unity]')) {
        logger.debug(`[Unity] Received unframed debug log: ${dataStr.trim()}`);
        // Don't process unframed logs as messages
        return;
      }
    }
    
    // Append new data to buffer
    this.messageBuffer = Buffer.concat([this.messageBuffer, data]);
    
    // Process complete messages
    while (this.messageBuffer.length >= 4) {
      // Read message length (first 4 bytes, big-endian)
      const messageLength = this.messageBuffer.readInt32BE(0);
      
      // Validate message length
      if (messageLength < 0 || messageLength > 1024 * 1024) { // Max 1MB messages
        logger.error(`[Unity] Invalid message length: ${messageLength}`);
        
        // Try to recover by looking for a valid framed message. Accept any length
        // up to the same 1MB protocol cap used above — the protocol supports large
        // responses (hierarchy dumps, scene analyses), so a 10KB ceiling here would
        // silently discard a legitimate large frame during recovery.
        // Scan the WHOLE buffer (not just the first 100 bytes) for a valid resync
        // point — a corrupt prefix can be followed by a large legitimate frame whose
        // header sits well past byte 100; capping the scan would discard it.
        let recoveryIndex = -1;
        for (let i = 4; i <= this.messageBuffer.length - 4; i++) {
          const testLength = this.messageBuffer.readInt32BE(i);
          if (testLength > 0 && testLength <= 1024 * 1024) {
            // Check if this could be a valid JSON message
            if (i + 4 + testLength <= this.messageBuffer.length) {
              const testData = this.messageBuffer.slice(i + 4, i + 4 + testLength).toString('utf8');
              if (testData.trim().startsWith('{')) {
                recoveryIndex = i;
                break;
              }
            }
          }
        }
        
        if (recoveryIndex > 0) {
          logger.warn(`[Unity] Discarding ${recoveryIndex} bytes of invalid data`);
          this.messageBuffer = this.messageBuffer.slice(recoveryIndex);
          continue;
        } else {
          // Can't recover, clear buffer
          logger.error('[Unity] Unable to recover from invalid frame, clearing buffer');
          this.messageBuffer = Buffer.alloc(0);
          break;
        }
      }
      
      // Check if we have the complete message
      if (this.messageBuffer.length >= 4 + messageLength) {
        // Extract message
        const messageData = this.messageBuffer.slice(4, 4 + messageLength);
        this.messageBuffer = this.messageBuffer.slice(4 + messageLength);
        
        // Process the message
        try {
          const message = messageData.toString('utf8');
          
          // Skip non-JSON messages (like debug logs)
          if (!message.trim().startsWith('{')) {
            logger.warn(`[Unity] Skipping non-JSON message: ${message.substring(0, 50)}...`);
            continue;
          }
          
          logger.info(`[Unity] Received framed message: ${message}`);
          
          const response = JSON.parse(message);
          logger.info(`[Unity] Parsed response:`, response);
          
          // Check if this is a response to a pending command
          if (response.id && this.pendingCommands.has(response.id)) {
            logger.info(`[Unity] Found pending command for ID ${response.id}`);
            const pending = this.pendingCommands.get(response.id);
            this.pendingCommands.delete(response.id);
            
            // Handle both old and new response formats
            if (response.status === 'success' || response.success === true) {
              logger.info(`[Unity] Command ${response.id} succeeded`);
              
              let result = response.result || response.data || {};
              
              // If result is a string, try to parse it as JSON
              if (typeof result === 'string') {
                try {
                  result = JSON.parse(result);
                  logger.info(`[Unity] Parsed string result as JSON:`, result);
                } catch (parseError) {
                  logger.warn(`[Unity] Failed to parse result as JSON: ${parseError.message}`);
                  // Keep the original string value
                }
              }
              
              if (isHandlerLevelError(result)) {
                const err = new Error(result.error);
                err.code = result.code || 'EDITOR_ERROR';
                logger.warn(`[Unity] Command ${response.id} returned a handler-level error under a success envelope: ${result.error}`);
                pending.reject(err);
              } else {
                logger.info(`[Unity] Command ${response.id} resolved successfully`);
                pending.resolve(result);
              }
            } else if (response.status === 'error' || response.success === false) {
              logger.error(`[Unity] Command ${response.id} failed:`, response.error);
              const err = new Error(response.error || 'Command failed');
              if (response.code) err.code = response.code;
              if (response.details !== undefined) err.details = response.details;
              pending.reject(err);
            } else {
              // Unknown format
              logger.warn(`[Unity] Command ${response.id} has unknown response format`);
              pending.resolve(response);
            }
          } else {
            // Handle unsolicited messages
            logger.info(`[Unity] Received unsolicited message:`, response);
            this.emit('message', response);
          }
        } catch (error) {
          logger.error('[Unity] Failed to parse response:', error.message);
          logger.debug(`[Unity] Raw message: ${messageData.toString().substring(0, 200)}...`);
          
          // Check if this looks like a Unity log message
          const messageStr = messageData.toString();
          if (messageStr.includes('[Unity Editor MCP]')) {
            logger.debug('[Unity] Received Unity log message instead of JSON response');
            // Don't treat this as a critical error
          }
        }
      } else {
        // Not enough data yet, wait for more
        break;
      }
    }
  }

  /**
   * Sends a command to Unity
   * @param {string} type - Command type
   * @param {object} params - Command parameters
   * @returns {Promise<any>} - Response from Unity
   */
  async sendCommand(type, params = {}) {
    logger.info(`[Unity] sendCommand called: ${type}`, { connected: this.connected, params });
    
    if (!this.connected) {
      logger.error('[Unity] Cannot send command - not connected');
      throw new Error('Not connected to Unity');
    }

    const id = String(++this.commandId);
    const command = {
      id,
      type,
      params
    };

    return new Promise((resolve, reject) => {
      logger.info(`[Unity] Setting up command ${id} with timeout ${config.unity.commandTimeout}ms`);
      
      // Set up timeout
      const timeout = setTimeout(() => {
        logger.error(`[Unity] Command ${id} timed out after ${config.unity.commandTimeout}ms`);
        this.pendingCommands.delete(id);
        reject(new Error('Command timeout'));
      }, config.unity.commandTimeout);

      // Store pending command
      this.pendingCommands.set(id, {
        resolve: (data) => {
          logger.info(`[Unity] Command ${id} resolved successfully`);
          clearTimeout(timeout);
          resolve(data);
        },
        reject: (error) => {
          logger.error(`[Unity] Command ${id} rejected with error:`, error.message);
          clearTimeout(timeout);
          reject(error);
        }
      });

      // Send command with framing
      const json = JSON.stringify(command);
      const messageBuffer = Buffer.from(json, 'utf8');
      const lengthBuffer = Buffer.allocUnsafe(4);
      lengthBuffer.writeInt32BE(messageBuffer.length, 0);
      
      const framedMessage = Buffer.concat([lengthBuffer, messageBuffer]);
      
      logger.info(`[Unity] Sending framed command ${id}: ${json}`);

      // Capture the socket up front: if a concurrent 'close' raced it to null, a
      // synchronous this.socket.write throw would escape the pending-cleanup below,
      // leaking the pendingCommands entry and its 30s timer.
      const sock = this.socket;
      if (!sock) {
        this.pendingCommands.delete(id);
        clearTimeout(timeout);
        reject(new Error('Socket closed before command could be sent'));
        return;
      }

      sock.write(framedMessage, (error) => {
        if (error) {
          logger.error(`[Unity] Failed to write command ${id}:`, error.message);
          this.pendingCommands.delete(id);
          clearTimeout(timeout);
          reject(error);
        } else {
          logger.info(`[Unity] Command ${id} written successfully, waiting for response...`);
        }
      });
    });
  }

  /**
   * Sends a ping command to Unity
   * @returns {Promise<any>}
   */
  async ping() {
    // Use normal command sending for ping with proper framing
    return this.sendCommand('ping', {});
  }

  /**
   * Checks if connected to Unity
   * @returns {boolean}
   */
  isConnected() {
    return this.connected;
  }
}