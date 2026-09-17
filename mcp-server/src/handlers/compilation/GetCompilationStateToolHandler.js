import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Node-logic override of the editor `get_compilation_state` command.
 *
 * Passthrough by default (single snapshot from the editor). With `waitForIdle: true` it becomes the
 * "compile-complete" primitive the agent needs after `refresh_assets`: it polls the editor's
 * get_compilation_state until compilation AND asset import have finished, TOLERATING the domain-reload
 * reconnect gap (the bridge drops for ~1s+ mid-reload — that gap is treated as "still busy", not an error),
 * then returns the final state. Compile errors are in `messages` (type "Error") + `errorCount` — the reliable
 * compile-error channel (the console log filter mis-classifies compile errors). This replaces sleep-and-hope.
 * (Production feedback #1/#3.)
 *
 * Server-side wait: an editor-side wait can't span the reload that kills it, so the wait lives here where the
 * connection survives + auto-reconnects.
 */
export class GetCompilationStateToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'get_compilation_state',
      'Get Unity compilation state + recent compile errors. With waitForIdle:true, blocks (server-side) until '
        + 'compilation and asset import finish — reconnecting across the domain reload — so you can call refresh_assets '
        + 'then wait instead of sleep-and-hope. Compile errors are in `messages` (type "Error") + `errorCount`.',
      {
        type: 'object',
        properties: {
          includeMessages: { type: 'boolean', default: true, description: 'Include the compile messages array.' },
          maxMessages: { type: 'number', default: 50, description: 'Cap on returned messages.' },
          waitForIdle: { type: 'boolean', default: false, description: 'Poll until compilation + asset import finish (tolerating the domain-reload reconnect), then return the final state.' },
          timeoutMs: { type: 'number', default: 120000, description: 'Max wait when waitForIdle (ms). On timeout the current state is returned with timedOut:true.' },
          pollIntervalMs: { type: 'number', default: 500, description: 'Poll interval when waitForIdle (ms).' },
          settleMs: { type: 'number', default: 2500, description: 'When waitForIdle and no compile has started yet, how long to wait for one to begin before concluding none was triggered (ms).' },
        },
      },
    );
    this.unityConnection = unityConnection;
  }

  validate(params) {
    for (const k of ['maxMessages', 'timeoutMs', 'pollIntervalMs', 'settleMs']) {
      if (params[k] !== undefined && (typeof params[k] !== 'number' || !(params[k] >= 0))) {
        throw new Error(`${k} must be a non-negative number`);
      }
    }
  }

  async execute(params) {
    const {
      includeMessages = true,
      maxMessages = 50,
      waitForIdle = false,
      timeoutMs = 120000,
      pollIntervalMs = 500,
      settleMs = 2500,
    } = params;

    const snapshot = () => this.unityConnection.sendCommand('get_compilation_state', { includeMessages, maxMessages });

    if (!waitForIdle) {
      if (!this.unityConnection.isConnected()) await this.unityConnection.connect();
      return await snapshot();
    }

    if (!this.unityConnection.isConnected()) {
      try { await this.unityConnection.connect(); } catch { /* mid-reload — the poll loop tolerates it */ }
    }

    const start = Date.now();
    const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
    let sawBusy = false;
    let last = null;
    let reconnectGaps = 0;

    for (;;) {
      const elapsed = Date.now() - start;
      if (elapsed >= timeoutMs) {
        return { ...(last || {}), waited: true, sawCompilation: sawBusy, timedOut: true, elapsedMs: elapsed, reconnectGaps };
      }

      // A disconnected bridge means the editor is mid-domain-reload/recompile — that IS "still busy". Don't issue a
      // command on a known-down connection (it would hang to the command timeout); treat the gap as busy and let the
      // connection auto-reconnect. (Feedback #1: the reload gap.)
      if (!this.unityConnection.isConnected()) {
        sawBusy = true;
        reconnectGaps++;
        await sleep(pollIntervalMs);
        continue;
      }

      let state;
      try {
        state = await snapshot();
      } catch {
        // The connection dropped mid-call (the reload began during the request). Same as above: still busy, retry.
        sawBusy = true;
        reconnectGaps++;
        await sleep(pollIntervalMs);
        continue;
      }

      last = state;
      const busy = !!(state && (state.isCompiling || state.isUpdating));
      if (busy) {
        sawBusy = true;
        await sleep(pollIntervalMs);
        continue;
      }

      // Idle. If we already saw compilation, it's finished — done.
      if (sawBusy) {
        return { ...state, waited: true, sawCompilation: true, timedOut: false, elapsedMs: Date.now() - start, reconnectGaps };
      }
      // Idle and never busy: compilation may not have started yet (refresh_assets is async). Wait out the settle
      // window before concluding nothing was triggered.
      if (elapsed >= settleMs) {
        return { ...state, waited: true, sawCompilation: false, timedOut: false, elapsedMs: Date.now() - start, reconnectGaps };
      }
      await sleep(pollIntervalMs);
    }
  }
}
