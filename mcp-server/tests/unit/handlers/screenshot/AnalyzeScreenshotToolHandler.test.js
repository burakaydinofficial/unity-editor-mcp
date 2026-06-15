import { describe, it, mock } from 'node:test';
import assert from 'node:assert/strict';
import { AnalyzeScreenshotToolHandler } from '../../../../src/handlers/screenshot/AnalyzeScreenshotToolHandler.js';

const makeConn = () => ({ isConnected: () => true, connect: async () => {}, sendCommand: mock.fn(async () => ({ success: true, width: 10, height: 10 })) });

describe('AnalyzeScreenshotToolHandler', () => {
  describe('path traversal rejection (security)', () => {
    it('rejects an imagePath with .. traversal and never calls sendCommand', async () => {
      const conn = makeConn();
      const h = new AnalyzeScreenshotToolHandler(conn);
      const res = await h.handle({ imagePath: 'Assets/../../etc/secret.png', analysisType: 'basic' });
      assert.equal(res.status, 'error');
      assert.match(res.error, /traversal|\.\./);
      assert.equal(conn.sendCommand.mock.calls.length, 0);
    });

    it('rejects a backslash .. traversal too', async () => {
      const h = new AnalyzeScreenshotToolHandler(makeConn());
      const res = await h.handle({ imagePath: 'Assets/..\\..\\secret.png', analysisType: 'basic' });
      assert.equal(res.status, 'error');
      assert.match(res.error, /traversal|\.\./);
    });

    it('accepts a normal Assets/ image path', () => {
      const h = new AnalyzeScreenshotToolHandler(makeConn());
      assert.doesNotThrow(() => h.validate({ imagePath: 'Assets/Screenshots/x.png', analysisType: 'basic' }));
    });
  });
});
