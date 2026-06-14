import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { isMetaTool, typedToolsExposed, filterListedTools, META_TOOL_NAMES } from '../../../src/core/toolExposure.js';

const defs = [
  { name: 'list_unity_instances' }, { name: 'list_unity_tools' }, { name: 'call_unity_tool' }, { name: 'set_active_unity_instance' },
  { name: 'ping' }, { name: 'create_gameobject' },
];

describe('toolExposure', () => {
  it('isMetaTool identifies the four instance meta-tools', () => {
    assert.equal(isMetaTool('call_unity_tool'), true);
    assert.equal(isMetaTool('ping'), false);
    assert.equal(META_TOOL_NAMES.size, 4);
  });

  it('typedToolsExposed honors the flag, and the default when unset', () => {
    for (const v of ['true', '1', 'all', 'on', 'yes', 'TRUE']) assert.equal(typedToolsExposed({ UNITY_MCP_TYPED_TOOLS: v }), true, v);
    for (const v of ['false', '0', 'none', 'off']) assert.equal(typedToolsExposed({ UNITY_MCP_TYPED_TOOLS: v }), false, v);
    assert.equal(typedToolsExposed({}, true), true);
    assert.equal(typedToolsExposed({}, false), false);
  });

  it('filterListedTools keeps everything when typed are exposed', () => {
    assert.equal(filterListedTools(defs, { UNITY_MCP_TYPED_TOOLS: 'true' }, false).length, 6);
  });

  it('filterListedTools keeps only the meta-tools when typed are not exposed', () => {
    const r = filterListedTools(defs, { UNITY_MCP_TYPED_TOOLS: 'false' }, true);
    assert.deepEqual(
      r.map((d) => d.name).sort(),
      ['call_unity_tool', 'list_unity_instances', 'list_unity_tools', 'set_active_unity_instance'],
    );
  });

  it('respects the default when the env var is unset', () => {
    assert.equal(filterListedTools(defs, {}, true).length, 6); // default expose-all
    assert.equal(filterListedTools(defs, {}, false).length, 4); // default meta-only
  });
});
