// Parse the bridge-ready line from the editor log tail, and poll until it appears. The editor logs
// "TcpTransport listening on 127.0.0.1:<port>" once its per-project TCP listener is armed (after each domain reload).
import { readFileSync, existsSync } from 'node:fs';
import { retry } from './retry.mjs';

// Anchor the port to end-of-line so a partially-flushed log line (the editor is actively writing) can't capture a
// truncated port. First match wins — the per-project port is stable across domain reloads (ADR 0003).
const RE = /TcpTransport listening on 127\.0\.0\.1:(\d+)(?=\r?\n)/;

export function bridgePort(logText) {
  const m = logText.match(RE);
  return m ? Number(m[1]) : null;
}

export async function waitForBridge(logPath, { timeoutMs = 180000, intervalMs = 1000 } = {}) {
  return retry(() => {
    const port = existsSync(logPath) ? bridgePort(readFileSync(logPath, 'utf8')) : null;
    if (port == null) throw new Error('bridge not up yet');
    return port;
  }, { timeoutMs, intervalMs });
}
