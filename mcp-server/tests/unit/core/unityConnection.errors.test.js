// Focused coverage for the error-classification branches of handleData. These
// drive handleData directly (insert a pending command, feed a framed response)
// so they do not depend on the connect() lifecycle — unlike the older
// unityConnection.test.js, whose connect()-based setup is stale.
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { UnityConnection } from '../../../src/core/unityConnection.js';

/** 4-byte big-endian length prefix + UTF-8 body (the wire framing). */
function frame(payload) {
  const body = Buffer.from(payload, 'utf8');
  const header = Buffer.alloc(4);
  header.writeUInt32BE(body.length, 0);
  return Buffer.concat([header, body]);
}

/** Registers a pending command and returns its settle promise. */
function expectResponse(connection, id) {
  return new Promise((resolve, reject) => {
    connection.pendingCommands.set(id, { resolve, reject });
  });
}

async function expectRejection(promise) {
  return promise.then(
    () => { throw new Error('expected rejection but resolved'); },
    (err) => err,
  );
}

describe('UnityConnection error-envelope handling', () => {
  it('propagates code and details from a status:error envelope', async () => {
    const connection = new UnityConnection();
    const settled = expectResponse(connection, '1');
    connection.handleData(frame(JSON.stringify({
      id: '1',
      status: 'error',
      error: 'GameObject not found',
      code: 'EDITOR_ERROR',
      details: { error: 'GameObject not found', context: 'Find' },
    })));
    const err = await expectRejection(settled);
    assert.equal(err.message, 'GameObject not found');
    assert.equal(err.code, 'EDITOR_ERROR');
    assert.equal(err.details.context, 'Find');
  });

  it('rejects without a code when none is provided', async () => {
    const connection = new UnityConnection();
    const settled = expectResponse(connection, '2');
    connection.handleData(frame(JSON.stringify({ id: '2', status: 'error', error: 'boom' })));
    const err = await expectRejection(settled);
    assert.equal(err.message, 'boom');
    assert.equal(err.code, undefined);
    assert.equal(err.details, undefined);
  });

  it('surfaces a handler-level error laundered under a success envelope', async () => {
    // Belt-and-braces for older editor packages that still wrap { error } in success.
    const connection = new UnityConnection();
    const settled = expectResponse(connection, '3');
    connection.handleData(frame(JSON.stringify({
      id: '3',
      status: 'success',
      result: { error: 'still an error', code: 'VALIDATION_ERROR' },
    })));
    const err = await expectRejection(settled);
    assert.equal(err.message, 'still an error');
    assert.equal(err.code, 'VALIDATION_ERROR');
  });

  it('resolves a genuine success result', async () => {
    const connection = new UnityConnection();
    const settled = expectResponse(connection, '4');
    connection.handleData(frame(JSON.stringify({ id: '4', status: 'success', result: { ok: true } })));
    const result = await settled;
    assert.deepEqual(result, { ok: true });
  });
});
