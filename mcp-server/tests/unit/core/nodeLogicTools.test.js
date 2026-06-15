import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { isNodeLogicTool, mergeNodeLogicSurface, NODE_LOGIC_TOOLS } from '../../../src/core/nodeLogicTools.js';
import { CreateScriptToolHandler } from '../../../src/handlers/scripting/CreateScriptToolHandler.js';

describe('nodeLogicTools', () => {
  it('identifies exactly the 3 Node-logic tools', () => {
    assert.equal(isNodeLogicTool('execute_menu_item'), true);
    assert.equal(isNodeLogicTool('create_script'), true);
    assert.equal(isNodeLogicTool('analyze_screenshot'), true);
    assert.equal(isNodeLogicTool('ping'), false);
    assert.equal(isNodeLogicTool('create_gameobject'), false);
    assert.equal(Object.keys(NODE_LOGIC_TOOLS).length, 3);
  });

  it('overrides a Node-logic entry in place with the Node handler schema, leaving others untouched', () => {
    const editorCreateScript = {
      name: 'create_script', category: 'scripting', description: 'editor version',
      params: { type: 'object', properties: { scriptContent: { type: 'string' } } },
    };
    const ping = { name: 'ping', category: 'system', description: 'p', params: { type: 'object' } };
    const merged = mergeNodeLogicSurface([ping, editorCreateScript]);

    assert.deepEqual(merged.find((t) => t.name === 'ping'), ping); // untouched
    const cs = merged.find((t) => t.name === 'create_script');
    const nodeDef = new CreateScriptToolHandler(null).getDefinition();
    assert.equal(cs.description, nodeDef.description);  // the Node handler's contract, not the editor's
    assert.deepEqual(cs.params, nodeDef.inputSchema);
    assert.equal(cs.category, 'scripting');
    assert.notDeepEqual(cs.params, editorCreateScript.params); // not the editor's scriptContent schema
  });

  it('does not append Node-logic tools the editor does not advertise', () => {
    const merged = mergeNodeLogicSurface([{ name: 'ping', category: 'system', description: 'p', params: {} }]);
    assert.equal(merged.length, 1);
    assert.equal(merged.find((t) => t.name === 'create_script'), undefined);
  });
});
