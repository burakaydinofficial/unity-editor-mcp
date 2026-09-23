import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Node-logic override of the editor `get_test_results` command.
 *
 * Passthrough by default (single snapshot). With `waitForCompletion: true` it becomes the "run and get results"
 * primitive: after `run_tests` (which is async), it polls the editor's get_test_results until the run finishes
 * (`isRunning: false`), TOLERATING the domain-reload reconnect gap that PlayMode / recompiling tests induce, then
 * returns the final results + summary. This replaces hand-polling + guessing at `isRunning`, and pairs with the
 * editor-side self-healing "running" state (a stale latch reads `isRunning: false`, so a dead run won't hang the wait).
 * (Production feedback: running/observing tests.)
 *
 * Server-side wait: an editor-side wait can't span the reload that kills it, so the wait lives here where the
 * connection survives + auto-reconnects.
 */
export class GetTestResultsToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'get_test_results',
      'Get the last test run\'s results. With waitForCompletion:true, polls (server-side) until the run finishes — '
        + 'tolerating the domain-reload reconnect (PlayMode / recompiling tests) — then returns the final results + '
        + 'summary, so you can run_tests then wait instead of polling by hand.',
      {
        type: 'object',
        properties: {
          includeDetails: { type: 'boolean', default: true, description: 'Include per-test message/stackTrace/output.' },
          filterStatus: { type: 'string', description: 'Only return results with this status (Passed/Failed/Skipped/Inconclusive).' },
          expectRunId: { type: 'string', description: 'The runId returned by run_tests; results carry runId + testMode, and runIdMismatch:true means the stored results are from a different run than you asked about.' },
          waitForCompletion: { type: 'boolean', default: false, description: 'Poll until the run finishes (isRunning=false), tolerating the domain-reload reconnect, then return the final results.' },
          timeoutMs: { type: 'number', default: 300000, description: 'Max wait when waitForCompletion (ms). On timeout the latest snapshot is returned with timedOut:true.' },
          pollIntervalMs: { type: 'number', default: 1000, description: 'Poll interval when waitForCompletion (ms).' },
        },
      },
    );
    this.unityConnection = unityConnection;
  }

  validate(params) {
    for (const k of ['timeoutMs', 'pollIntervalMs']) {
      if (params[k] !== undefined && (typeof params[k] !== 'number' || !(params[k] >= 0))) {
        throw new Error(`${k} must be a non-negative number`);
      }
    }
  }

  async execute(params) {
    const { includeDetails = true, filterStatus, expectRunId, waitForCompletion = false, timeoutMs = 300000, pollIntervalMs = 1000 } = params;
    const snapParams = { includeDetails };
    if (filterStatus !== undefined) snapParams.filterStatus = filterStatus;
    if (expectRunId !== undefined) snapParams.expectRunId = expectRunId;
    const snapshot = () => this.unityConnection.sendCommand('get_test_results', snapParams);

    if (!waitForCompletion) {
      if (!this.unityConnection.isConnected()) await this.unityConnection.connect();
      return await snapshot();
    }

    if (!this.unityConnection.isConnected()) {
      try { await this.unityConnection.connect(); } catch { /* mid-reload — the poll loop tolerates it */ }
    }

    const start = Date.now();
    const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
    let last = null;
    let reconnectGaps = 0;

    for (;;) {
      const elapsed = Date.now() - start;
      if (elapsed >= timeoutMs) {
        return { ...(last || {}), waited: true, timedOut: true, elapsedMs: elapsed, reconnectGaps };
      }

      // A disconnected bridge means the editor is mid domain-reload (PlayMode / recompiling tests) — the run is still
      // going. Don't issue on a known-down connection; treat the gap as "still running" and let it auto-reconnect.
      if (!this.unityConnection.isConnected()) {
        reconnectGaps++;
        await sleep(pollIntervalMs);
        continue;
      }

      let state;
      try {
        state = await snapshot();
      } catch {
        reconnectGaps++;
        await sleep(pollIntervalMs);
        continue;
      }

      last = state;
      if (state && state.isRunning) {
        await sleep(pollIntervalMs);
        continue;
      }
      // isRunning:false — the run finished (or none was active; the editor's self-heal makes a stale latch read false).
      return { ...state, waited: true, timedOut: false, elapsedMs: Date.now() - start, reconnectGaps };
    }
  }
}
