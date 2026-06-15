import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { SetProjectSettingToolHandler } from '../../../src/handlers/editor/SetProjectSettingToolHandler.js';
import { ManagePackagesToolHandler } from '../../../src/handlers/editor/ManagePackagesToolHandler.js';
import { QuitEditorToolHandler } from '../../../src/handlers/system/QuitEditorToolHandler.js';
import { GetSymbolsToolHandler } from '../../../src/handlers/code/GetSymbolsToolHandler.js';
import { FindSymbolToolHandler } from '../../../src/handlers/code/FindSymbolToolHandler.js';
import { FindReferencesToolHandler } from '../../../src/handlers/code/FindReferencesToolHandler.js';
import { GetSymbolBodyToolHandler } from '../../../src/handlers/code/GetSymbolBodyToolHandler.js';

const fakeConn = (sendImpl) => ({
  isConnected: () => true,
  sendCommand: sendImpl || (async () => ({ ok: true })),
});

const cases = [
  { Cls: SetProjectSettingToolHandler, name: 'set_project_setting', sample: { key: 'productName', value: 'X' }, required: ['key', 'value'] },
  { Cls: ManagePackagesToolHandler, name: 'manage_packages', sample: { action: 'add', packageId: 'com.x.y' }, required: ['action', 'packageId'] },
  { Cls: QuitEditorToolHandler, name: 'quit_editor', sample: {}, required: [] },
  { Cls: GetSymbolsToolHandler, name: 'get_symbols', sample: { path: 'Assets/X.cs' }, required: ['path'] },
  { Cls: FindSymbolToolHandler, name: 'find_symbol', sample: { name: 'Foo' }, required: ['name'] },
  { Cls: FindReferencesToolHandler, name: 'find_references', sample: { name: 'Foo' }, required: ['name'] },
  { Cls: GetSymbolBodyToolHandler, name: 'get_symbol_body', sample: { path: 'Assets/X.cs', name: 'Foo' }, required: ['path', 'name'] },
];

for (const { Cls, name, sample, required } of cases) {
  describe(name, () => {
    it('exposes the right name, description, and required fields', () => {
      const h = new Cls(fakeConn());
      assert.equal(h.name, name);
      assert.ok(h.description && h.description.length > 0);
      assert.equal(h.inputSchema.type, 'object');
      assert.deepEqual(h.inputSchema.required, required);
    });

    it('routes to sendCommand with the command type + params and returns the result', async () => {
      let sent;
      const h = new Cls(fakeConn(async (type, p) => { sent = { type, p }; return { value: 1 }; }));
      const r = await h.execute(sample);
      assert.equal(sent.type, name);
      assert.deepEqual(sent.p, sample);
      assert.deepEqual(r, { value: 1 });
    });

    it('throws when no editor is connected', async () => {
      const h = new Cls({ isConnected: () => false, sendCommand: async () => ({}) });
      await assert.rejects(() => h.execute(sample), /not available/);
    });
  });
}
