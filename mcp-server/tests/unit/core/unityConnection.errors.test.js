// Focused coverage for the error-classification branches of handleData. These
// drive handleData directly (insert a pending command, feed a framed response)
// so they do not depend on the connect() lifecycle — unlike the older
// unityConnection.test.js, whose connect()-based setup is stale.
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir, hostname } from 'node:os';
import { join } from 'node:path';
import { UnityConnection } from '../../../src/core/unityConnection.js';
import { instanceFileName, derivePort } from '../../../src/core/discovery.js';

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

describe('UnityConnection.resolveTargetPort', () => {
  it('explicit UNITY_PORT short-circuits the registry', () => {
    const connection = new UnityConnection();
    assert.strictEqual(connection.resolveTargetPort({ UNITY_PORT: '7777' }), 7777);
  });

  it('re-resolves a moved editor via the registry (live descriptor wins)', () => {
    const dir = mkdtempSync(join(tmpdir(), 'mcp-conn-'));
    try {
      const projectPath = 'C:/projects/game';
      const descriptor = {
        schemaVersion: 1,
        projectPath,
        port: 6890, // an "ephemeral" port the editor moved to
        pid: process.pid, // alive
        host: hostname(),
        unityVersion: '2020.3.49f1',
        protocolVersion: '1.0.0',
        startedAt: new Date().toISOString(),
        lastHeartbeat: new Date().toISOString(),
      };
      writeFileSync(join(dir, instanceFileName(projectPath)), JSON.stringify(descriptor));
      const connection = new UnityConnection();
      const port = connection.resolveTargetPort({ UNITY_PROJECT_PATH: projectPath, UNITY_MCP_REGISTRY_DIR: dir });
      assert.strictEqual(port, 6890);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it('falls back to the derived port when no live descriptor exists', () => {
    const dir = mkdtempSync(join(tmpdir(), 'mcp-conn-'));
    try {
      const connection = new UnityConnection();
      const port = connection.resolveTargetPort({ UNITY_PROJECT_PATH: 'C:/projects/none', UNITY_MCP_REGISTRY_DIR: dir });
      assert.strictEqual(port, derivePort('C:/projects/none'));
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
