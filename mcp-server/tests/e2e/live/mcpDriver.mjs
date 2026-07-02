// Drives the editor through the REAL MCP chain (client -> mcp-server subprocess -> editor), the same path an agent
// uses. The server's own auto-reconnect handles the editor's domain-reload TCP drop; call() just retries across it.
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { retry } from './retry.mjs';

const SERVER = resolve(dirname(fileURLToPath(import.meta.url)), '../../../src/core/server.js');

export class McpDriver {
  // `port` is the editor's derived per-project TCP port (from waitForBridge). call_unity_tool REQUIRES an instance,
  // and the numeric `instance` (the port) is what actually resolves the target connection. UNITY_HOST pins the
  // loopback literal so the server doesn't DNS-resolve 'localhost' to ::1 against an IPv4-only editor. NODE_ENV=test /
  // CI would make UnityConnection refuse to connect, so they are cleared.
  async start({ port }) {
    this.instance = String(port);
    const env = { ...process.env, UNITY_PORT: this.instance, UNITY_HOST: '127.0.0.1' };
    delete env.NODE_ENV;
    delete env.CI;
    delete env.DISABLE_AUTO_RECONNECT; // keep auto-reconnect ON so it survives domain reloads
    this.transport = new StdioClientTransport({ command: 'node', args: [SERVER], env });
    this.client = new Client({ name: 'e2e-live', version: '1.0.0' }, { capabilities: {} });
    await this.client.connect(this.transport);
  }

  // Invoke an editor tool. Returns the MCP result ({ content: [{ type:'text', text: <JSON> }], isError }).
  // Verify-by-outcome: never trust a transition's own response — a reload can swallow it — assert the effect after.
  //
  // retry re-issues the IDENTICAL call, so it is safe ONLY for IDEMPOTENT tools (play_game / stop_game /
  // get_editor_state / create_script / refresh_assets / ...). For a TOGGLE like pause_game (isPaused = !isPaused),
  // pass { once: true } so a lost response is NOT retried — a retry would toggle the state back.
  async call(toolName, args = {}, { timeoutMs = 90000, once = false } = {}) {
    const attempt = async () => {
      const res = await this.client.callTool({
        name: 'call_unity_tool',
        arguments: { instance: this.instance, tool: toolName, params: args },
      });
      if (res.isError) throw new Error(`tool ${toolName} error: ${JSON.stringify(res.content)}`);
      return res;
    };
    return once ? attempt() : retry(attempt, { timeoutMs, intervalMs: 750 });
  }

  async stop() { try { await this.client?.close(); } catch {} }
}

// Convenience: parse the single text content of a call() result as JSON.
export function resultJson(res) {
  return JSON.parse(res.content[0].text);
}
