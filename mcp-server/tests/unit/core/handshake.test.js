import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  parseSemver,
  checkProtocolCompatibility,
  evaluateHandshake,
  performHandshake,
  PROTOCOL_VERSION,
} from '../../../src/core/handshake.js';

describe('handshake', () => {
  describe('parseSemver', () => {
    it('parses valid versions and rejects malformed ones', () => {
      assert.deepEqual(parseSemver('1.2.3'), { major: 1, minor: 2, patch: 3 });
      assert.equal(parseSemver('1.2'), null);
      assert.equal(parseSemver('x.y.z'), null);
      assert.equal(parseSemver(null), null);
    });
  });

  describe('checkProtocolCompatibility', () => {
    it('treats a shared major (with minor/patch skew) as compatible', () => {
      assert.equal(checkProtocolCompatibility('1.0.0', '1.0.0').compatible, true);
      assert.equal(checkProtocolCompatibility('1.4.0', '1.2.7').compatible, true);
    });

    it('rejects a major mismatch with PROTOCOL_VERSION_MISMATCH', () => {
      const r = checkProtocolCompatibility('2.0.0', '1.9.9');
      assert.equal(r.compatible, false);
      assert.equal(r.code, 'PROTOCOL_VERSION_MISMATCH');
    });

    it('rejects unparseable versions', () => {
      assert.equal(checkProtocolCompatibility('bad', '1.0.0').compatible, false);
      assert.equal(checkProtocolCompatibility('1.0', '1.0.0').code, 'PROTOCOL_VERSION_MISMATCH');
    });
  });

  describe('evaluateHandshake', () => {
    const handshake = (over = {}) => ({
      protocolVersion: PROTOCOL_VERSION,
      unityVersion: '2020.3.49f1',
      projectPath: 'C:/projects/game',
      availableCommands: ['ping'],
      ...over,
    });

    it('is compatible for a matching protocol and project', () => {
      const r = evaluateHandshake(handshake(), { expectedProjectPath: 'C:\\Projects\\Game\\' });
      assert.equal(r.compatible, true);
    });

    it('flags a protocol major mismatch', () => {
      const r = evaluateHandshake(handshake({ protocolVersion: '2.0.0' }));
      assert.equal(r.compatible, false);
      assert.equal(r.code, 'PROTOCOL_VERSION_MISMATCH');
    });

    it('classifies a missing protocol version as NO_PROTOCOL_VERSION, not a mismatch', () => {
      const r = evaluateHandshake(handshake({ protocolVersion: undefined }));
      assert.equal(r.compatible, false);
      assert.equal(r.code, 'NO_PROTOCOL_VERSION');
    });

    it('flags a wrong-project connection', () => {
      const r = evaluateHandshake(handshake({ projectPath: 'C:/projects/other' }), {
        expectedProjectPath: 'C:/projects/game',
      });
      assert.equal(r.compatible, false);
      assert.equal(r.code, 'PROJECT_PATH_MISMATCH');
    });

    it('skips the project check when no expected path is given', () => {
      assert.equal(evaluateHandshake(handshake({ projectPath: 'anything' })).compatible, true);
    });

    it('rejects a missing payload', () => {
      assert.equal(evaluateHandshake(null).compatible, false);
    });
  });

  describe('performHandshake', () => {
    const okHandshake = {
      protocolVersion: PROTOCOL_VERSION,
      unityVersion: '2020.3.49f1',
      projectPath: 'C:/projects/game',
      availableCommands: ['ping'],
    };
    const mockConn = (impl) => ({ sendCommand: async (type) => impl(type) });

    it('performs and reports compatible for a matching editor', async () => {
      const r = await performHandshake(mockConn(() => okHandshake), { expectedProjectPath: 'C:/projects/game' });
      assert.equal(r.performed, true);
      assert.equal(r.compatible, true);
      assert.deepEqual(r.handshake, okHandshake);
    });

    it('preserves the advertised commands manifest in the returned handshake', async () => {
      const withManifest = {
        ...okHandshake,
        commands: [{ name: 'ping', description: 'Test connection', params: { type: 'object' } }],
      };
      const r = await performHandshake(mockConn(() => withManifest), { expectedProjectPath: 'C:/projects/game' });
      assert.equal(r.performed, true);
      assert.equal(r.compatible, true);
      assert.equal(r.handshake.commands.length, 1);
      assert.equal(r.handshake.commands[0].name, 'ping');
      assert.deepEqual(r.handshake.commands[0].params, { type: 'object' });
    });

    it('reports a protocol mismatch without throwing', async () => {
      const r = await performHandshake(mockConn(() => ({ ...okHandshake, protocolVersion: '2.0.0' })));
      assert.equal(r.performed, true);
      assert.equal(r.compatible, false);
      assert.equal(r.code, 'PROTOCOL_VERSION_MISMATCH');
    });

    it('reports a project mismatch when an expected path is given', async () => {
      const r = await performHandshake(mockConn(() => okHandshake), { expectedProjectPath: 'C:/projects/other' });
      assert.equal(r.compatible, false);
      assert.equal(r.code, 'PROJECT_PATH_MISMATCH');
    });

    it('degrades gracefully for an editor predating the command', async () => {
      const r = await performHandshake(mockConn(() => {
        const e = new Error('Unknown command type: handshake');
        e.code = 'UNKNOWN_COMMAND';
        throw e;
      }));
      assert.equal(r.performed, false);
      assert.equal(r.reason, 'unsupported');
    });

    it('degrades gracefully on a transport error', async () => {
      const r = await performHandshake(mockConn(() => { throw new Error('socket closed'); }));
      assert.equal(r.performed, false);
      assert.equal(r.reason, 'error');
    });
  });
});
