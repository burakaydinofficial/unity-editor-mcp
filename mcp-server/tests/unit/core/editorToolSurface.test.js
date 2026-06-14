import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { editorToolSurface } from '../../../src/core/editorToolSurface.js';

describe('editorToolSurface', () => {
  it('uses the rich manifest when present (hasSchemas true)', () => {
    const info = { commands: [{ name: 'ping', category: 'system', description: 'Test', params: { type: 'object' } }] };
    const r = editorToolSurface(info);
    assert.equal(r.hasSchemas, true);
    assert.equal(r.tools.length, 1);
    assert.deepEqual(r.tools[0], { name: 'ping', category: 'system', description: 'Test', params: { type: 'object' } });
  });

  it('falls back to availableCommands names when no rich manifest (hasSchemas false, params null)', () => {
    const r = editorToolSurface({ availableCommands: ['ping', 'get_editor_state'] });
    assert.equal(r.hasSchemas, false);
    assert.deepEqual(r.tools.map((t) => t.name), ['ping', 'get_editor_state']);
    assert.equal(r.tools[0].params, null);
    assert.equal(r.tools[0].category, null);
  });

  it('prefers the rich manifest even when availableCommands is also present', () => {
    const r = editorToolSurface({ commands: [{ name: 'ping', params: { type: 'object' } }], availableCommands: ['ping', 'old_only'] });
    assert.equal(r.hasSchemas, true);
    assert.deepEqual(r.tools.map((t) => t.name), ['ping']);
  });

  it('returns an empty surface for null/empty editorInfo', () => {
    assert.deepEqual(editorToolSurface(null), { tools: [], hasSchemas: false });
    assert.deepEqual(editorToolSurface({}), { tools: [], hasSchemas: false });
    assert.deepEqual(editorToolSurface({ commands: [], availableCommands: [] }), { tools: [], hasSchemas: false });
  });
});
