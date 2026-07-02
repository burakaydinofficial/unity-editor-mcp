import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bridgePort } from '../../e2e/live/waitForBridge.mjs';

test('bridgePort extracts the port', () => {
  assert.equal(bridgePort('...\n[UnityEditorMCP] TcpTransport listening on 127.0.0.1:6423\n...'), 6423);
});
test('bridgePort returns null before the line appears', () => {
  assert.equal(bridgePort('booting...\n'), null);
});
