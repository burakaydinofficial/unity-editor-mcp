// Parse the bridge-ready line from the editor log tail, and poll until it appears. The editor logs
// "TcpTransport listening on 127.0.0.1:<port>" once its per-project TCP listener is armed (after each domain reload).
import { readFileSync, existsSync } from 'node:fs';
import { retry } from './retry.mjs';

const RE = /TcpTransport listening on 127\.0\.0\.1:(\d+)/;

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
