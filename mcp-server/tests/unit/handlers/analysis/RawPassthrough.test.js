import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { AnalyzeSceneContentsToolHandler } from '../../../../src/handlers/analysis/AnalyzeSceneContentsToolHandler.js';
import { GetGameObjectDetailsToolHandler } from '../../../../src/handlers/analysis/GetGameObjectDetailsToolHandler.js';

// These two analysis handlers used to return only `result.summary` wrapped in an
// MCP { content } shape — discarding the payload and double-wrapping it. They now
// return the editor's raw payload, which handle() wraps once in { status, result }.
const mockConn = (impl) => ({ isConnected: () => true, sendCommand: async (type, args) => impl(type, args) });

describe('analysis handlers raw passthrough (no payload discard / double-wrap)', () => {
  it('analyze_scene_contents returns the full payload, not just summary', async () => {
    const full = { summary: '3 objects', gameObjects: [{ name: 'A' }, { name: 'B' }], total: 2 };
    const handler = new AnalyzeSceneContentsToolHandler(mockConn((type) => {
      assert.equal(type, 'analyze_scene_contents');
      return full;
    }));
    const response = await handler.handle({});
    assert.equal(response.status, 'success');
    assert.deepEqual(response.result, full);
    assert.ok(!('content' in response.result), 'must not be an MCP content wrapper');
  });

  it('get_gameobject_details returns the full payload', async () => {
    const full = { summary: 'Cube', name: 'Cube', components: [{ type: 'Transform' }] };
    const handler = new GetGameObjectDetailsToolHandler(mockConn(() => full));
    const response = await handler.handle({ gameObjectName: 'Cube' });
    assert.equal(response.status, 'success');
    assert.deepEqual(response.result, full);
  });

  it('surfaces a handler-level error as a real error envelope', async () => {
    const handler = new AnalyzeSceneContentsToolHandler(mockConn(() => {
      const e = new Error('No active scene');
      e.code = 'EDITOR_ERROR';
      throw e;
    }));
    const response = await handler.handle({});
    assert.equal(response.status, 'error');
    assert.equal(response.error, 'No active scene');
    assert.equal(response.code, 'EDITOR_ERROR');
  });

  it('errors when Unity is not connected', async () => {
    const handler = new GetGameObjectDetailsToolHandler({ isConnected: () => false, sendCommand: async () => {} });
    const response = await handler.handle({ gameObjectName: 'Cube' });
    assert.equal(response.status, 'error');
    assert.match(response.error, /not available/);
  });
});
