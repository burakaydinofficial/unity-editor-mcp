import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  parseSemver,
  checkProtocolCompatibility,
  evaluateHandshake,
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
});
