import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { LoadSceneToolHandler } from '../../../../src/handlers/scene/LoadSceneToolHandler.js';
import { CreateSceneToolHandler } from '../../../../src/handlers/scene/CreateSceneToolHandler.js';
import { SaveSceneToolHandler } from '../../../../src/handlers/scene/SaveSceneToolHandler.js';

// Regression guard for the batch-A double-unwrap fix: these handlers used to read
// `result.result`, but sendCommand already unwraps the wire envelope and resolves
// with the payload directly — so `result.result` was always undefined and the
// handlers returned a params-echo stub instead of the real scene data. They now
// return the editor payload directly, which handle() wraps once in { status, result }.
const mockConn = (impl) => ({ isConnected: () => true, sendCommand: async (type, args) => impl(type, args) });

describe('scene handlers return the editor payload (no double-unwrap stub)', () => {
  it('load_scene returns the full payload, not a params stub', async () => {
    const full = { sceneName: 'Main', scenePath: 'Assets/Main.unity', loadMode: 'Single', isLoaded: true };
    const handler = new LoadSceneToolHandler(mockConn((type) => {
      assert.equal(type, 'load_scene');
      return full;
    }));
    const response = await handler.handle({ scenePath: 'Assets/Main.unity' });
    assert.equal(response.status, 'success');
    assert.deepEqual(response.result, full);
  });

  it('create_scene returns the full payload', async () => {
    const full = { sceneName: 'New', path: 'Assets/Scenes/New.unity', isLoaded: true, sceneIndex: 0 };
    const handler = new CreateSceneToolHandler(mockConn((type) => {
      assert.equal(type, 'create_scene');
      return full;
    }));
    const response = await handler.handle({ sceneName: 'New' });
    assert.equal(response.status, 'success');
    assert.deepEqual(response.result, full);
  });

  it('save_scene returns the full payload (was missed in batch A)', async () => {
    const full = { sceneName: 'Main', scenePath: 'Assets/Main.unity', saved: true, isDirty: false, summary: 'Saved Main' };
    const handler = new SaveSceneToolHandler(mockConn((type) => {
      assert.equal(type, 'save_scene');
      return full;
    }));
    const response = await handler.handle({});
    assert.equal(response.status, 'success');
    assert.deepEqual(response.result, full);
  });

  it('load_scene surfaces a handler-level error as an error envelope', async () => {
    const handler = new LoadSceneToolHandler(mockConn(() => {
      const e = new Error('Scene not found');
      e.code = 'UNITY_ERROR';
      throw e;
    }));
    const response = await handler.handle({ scenePath: 'Assets/Missing.unity' });
    assert.equal(response.status, 'error');
    assert.match(response.error, /Scene not found/);
  });

  it('load_scene errors when Unity is not connected', async () => {
    const handler = new LoadSceneToolHandler({ isConnected: () => false, sendCommand: async () => {} });
    const response = await handler.handle({ scenePath: 'Assets/Main.unity' });
    assert.equal(response.status, 'error');
    assert.match(response.error, /not available/);
  });
});
