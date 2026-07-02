import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { bridgePort, waitForBridge } from '../../e2e/live/waitForBridge.mjs';

const dir = mkdtempSync(join(tmpdir(), 'e2e-wb-'));
after(() => rmSync(dir, { recursive: true, force: true }));

test('bridgePort extracts the port', () => {
  assert.equal(bridgePort('...\n[UnityEditorMCP] TcpTransport listening on 127.0.0.1:6423\n...'), 6423);
});

test('bridgePort returns null before the line appears', () => {
  assert.equal(bridgePort('booting...\n'), null);
});

test('bridgePort: first line wins; partial / no-port / non-loopback -> null', () => {
  assert.equal(bridgePort('TcpTransport listening on 127.0.0.1:6423\nTcpTransport listening on 127.0.0.1:7093\n'), 6423);
  assert.equal(bridgePort('TcpTransport listening on 127.0.0.1:64'), null);   // partial (no EOL after digits)
  assert.equal(bridgePort('TcpTransport listening on 127.0.0.1\n'), null);    // no port
  assert.equal(bridgePort('TcpTransport listening on 0.0.0.0:6423\n'), null); // non-loopback
});

test('waitForBridge resolves the port from a written log', async () => {
  const log = join(dir, 'ok.log');
  writeFileSync(log, 'TcpTransport listening on 127.0.0.1:6423\n');
  assert.equal(await waitForBridge(log, { timeoutMs: 500, intervalMs: 5 }), 6423);
});

test('waitForBridge rejects (does not hang) on a missing log past timeout', async () => {
  await assert.rejects(waitForBridge(join(dir, 'nope.log'), { timeoutMs: 30, intervalMs: 5 }));
});
