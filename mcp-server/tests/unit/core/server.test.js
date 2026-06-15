import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from '../../../src/core/server.js';
import { createHandlers, BaseToolHandler } from '../../../src/handlers/index.js';

describe('Server', () => {
  let testSetup;
  let server;
  let manager;
  const mockConfig = { server: { name: 'test-unity-mcp', version: '1.0.0' } };

  beforeEach(async () => {
    testSetup = await createServer(mockConfig);
    server = testSetup.server;
    manager = testSetup.manager;
  });

  afterEach(async () => {
    if (manager && typeof manager.disconnectAll === 'function') manager.disconnectAll();
    if (server && server.close) await server.close();
  });

  describe('createServer', () => {
    it('creates a server + connection manager (no default connection — ADR 0006)', () => {
      assert.ok(server);
      assert.ok(manager);
      assert.equal(typeof server.connect, 'function');
      assert.equal(typeof server.setRequestHandler, 'function');
      assert.equal(testSetup.unityConnection, undefined); // there is no active/default connection
    });
  });

  describe('Handler registration', () => {
    it('registers exactly the 3 generic meta-tools (ADR 0006)', () => {
      const handlers = createHandlers(manager);
      assert.ok(handlers instanceof Map);
      assert.equal(handlers.size, 3);
      assert.ok(handlers.has('list_unity_instances'));
      assert.ok(handlers.has('list_unity_tools'));
      assert.ok(handlers.has('call_unity_tool'));
      // The editor commands + the Node-logic tools are reached via call_unity_tool, not advertised.
      assert.ok(!handlers.has('ping'));
      assert.ok(!handlers.has('create_gameobject'));
      assert.ok(!handlers.has('create_script'));
    });

    it('handlers have the correct structure', () => {
      const handlers = createHandlers(manager);
      for (const [name, handler] of handlers) {
        assert.equal(handler.name, name);
        assert.ok(handler.description);
        assert.ok(handler.inputSchema);
        assert.equal(typeof handler.handle, 'function');
        assert.equal(typeof handler.execute, 'function');
        assert.equal(typeof handler.validate, 'function');
        assert.equal(typeof handler.getDefinition, 'function');
      }
    });
  });

  describe('Error handling', () => {
    it('wraps a handler exception in an error envelope with the tool name', async () => {
      class ErrorHandler extends BaseToolHandler {
        constructor() { super('error_test', 'Test error handling'); }
        async execute() { throw new Error('Test error'); }
      }
      const result = await new ErrorHandler().handle({});
      assert.equal(result.status, 'error');
      assert.equal(result.error, 'Test error');
      assert.equal(result.details.tool, 'error_test');
    });

    it('includes a custom error code when available', async () => {
      class CodedErrorHandler extends BaseToolHandler {
        constructor() { super('coded_error_test', 'Test coded error'); }
        async execute() { const e = new Error('Coded error'); e.code = 'CUSTOM_ERROR_CODE'; throw e; }
      }
      const result = await new CodedErrorHandler().handle({});
      assert.equal(result.status, 'error');
      assert.equal(result.code, 'CUSTOM_ERROR_CODE');
    });
  });
});
