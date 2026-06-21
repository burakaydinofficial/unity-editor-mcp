import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { createServer, toMcpResponse } from '../../../src/core/server.js';
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

describe('toMcpResponse — image content (G5)', () => {
  it('emits an MCP image block + a text echo with the base64 stripped to a size marker', () => {
    const data = 'AAAABBBBCCCC'; // stand-in base64
    const out = toMcpResponse({ status: 'success', result: { captureMode: 'camera', path: 'Assets/x.png', image: { mimeType: 'image/png', data } } }, 'capture_screenshot');
    assert.equal(out.content.length, 2);
    const image = out.content.find((c) => c.type === 'image');
    const text = out.content.find((c) => c.type === 'text');
    assert.ok(image, 'has an image block');
    assert.equal(image.data, data);
    assert.equal(image.mimeType, 'image/png');
    assert.ok(!text.text.includes(data), 'raw base64 is NOT duplicated in the text');
    assert.ok(text.text.includes('"bytes"'), 'text carries a size marker');
    assert.ok(text.text.includes('camera'), 'text keeps the other fields');
  });

  it('defaults the mimeType to image/png when omitted', () => {
    const out = toMcpResponse({ status: 'success', result: { image: { data: 'ZZZZ' } } }, 'capture_screenshot');
    assert.equal(out.content.find((c) => c.type === 'image').mimeType, 'image/png');
  });

  it('leaves a normal result as a single text block', () => {
    const out = toMcpResponse({ status: 'success', result: { count: 3, objects: [] } }, 'find_gameobject');
    assert.equal(out.content.length, 1);
    assert.equal(out.content[0].type, 'text');
  });

  it('ignores a non-string / empty image.data (no image block)', () => {
    assert.equal(toMcpResponse({ status: 'success', result: { image: { data: '' } } }, 't').content[0].type, 'text');
    assert.equal(toMcpResponse({ status: 'success', result: { image: { data: 123 } } }, 't').content[0].type, 'text');
  });

  it('keeps an error result as an isError text response', () => {
    const out = toMcpResponse({ status: 'error', error: 'nope', code: 'X' }, 't');
    assert.equal(out.isError, true);
    assert.equal(out.content[0].type, 'text');
  });
});
