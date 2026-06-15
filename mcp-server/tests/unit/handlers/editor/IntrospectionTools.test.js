import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { GetEditorInfoToolHandler } from '../../../../src/handlers/system/GetEditorInfoToolHandler.js';
import { GetProjectSettingsToolHandler } from '../../../../src/handlers/editor/GetProjectSettingsToolHandler.js';
import { ListPackagesToolHandler } from '../../../../src/handlers/editor/ListPackagesToolHandler.js';

const fakeConn = (sendImpl) => ({
  isConnected: () => true,
  sendCommand: sendImpl || (async (type, p) => ({ ok: true, type, p })),
});

const cases = [
  { Cls: GetEditorInfoToolHandler, name: 'get_editor_info' },
  { Cls: GetProjectSettingsToolHandler, name: 'get_project_settings' },
  { Cls: ListPackagesToolHandler, name: 'list_packages' },
];

for (const { Cls, name } of cases) {
  describe(name, () => {
    it('exposes the right name and a no-arg schema', () => {
      const h = new Cls(fakeConn());
      assert.equal(h.name, name);
      assert.equal(h.inputSchema.type, 'object');
      assert.deepEqual(h.inputSchema.required, []);
    });

    it('routes to sendCommand with the command type and returns the result', async () => {
      let sent;
      const h = new Cls(fakeConn(async (type, p) => { sent = { type, p }; return { value: 1 }; }));
      const r = await h.execute({});
      assert.equal(sent.type, name);
      assert.deepEqual(r, { value: 1 });
    });

    it('throws when no editor is connected', async () => {
      const h = new Cls({ isConnected: () => false, sendCommand: async () => ({}) });
      await assert.rejects(() => h.execute({}), /not available/);
    });
  });
}
