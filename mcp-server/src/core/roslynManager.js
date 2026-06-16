/**
 * Per-instance Roslyn sidecar state (ADR 0006 capability handshake). The sidecar process itself is
 * Plan 3; this holds the state machine and an injectable client factory. The DEFAULT factory produces
 * no client (returns null) → start() resolves to 'unavailable', so the framework ships inert until the
 * sidecar backend is installed. Keyed by the resolved connection key (host:port) — Roslyn state is
 * per-editor.
 */
export const ROSLYN_STATES = Object.freeze({ OFF: 'off', INDEXING: 'indexing', READY: 'ready', UNAVAILABLE: 'unavailable' });

// Default: no sidecar backend present. Plan 3 swaps in a factory that spawns + connects the .NET process.
async function defaultClientFactory() { return null; }

export class RoslynManager {
  constructor(clientFactory = defaultClientFactory) {
    this._byKey = new Map(); // key -> { state, client, error }
    this._clientFactory = clientFactory;
  }

  getState(key) { return this._byKey.get(key)?.state ?? ROSLYN_STATES.OFF; }
  isReady(key) { return this.getState(key) === ROSLYN_STATES.READY; }
  client(key) { return this._byKey.get(key)?.client ?? null; }
  statusOf(key) {
    const e = this._byKey.get(key);
    return { state: e?.state ?? ROSLYN_STATES.OFF, error: e?.error ?? null };
  }

  async start(key, conn) {
    const existing = this._byKey.get(key);
    if (existing && (existing.state === ROSLYN_STATES.READY || existing.state === ROSLYN_STATES.INDEXING)) {
      return existing.state; // idempotent
    }
    const token = Symbol('roslyn-start'); // detect a stop()/restart that races during the async factory
    this._byKey.set(key, { state: ROSLYN_STATES.INDEXING, client: null, token });
    try {
      const client = await this._clientFactory(conn);
      // If stop() (or a newer start()) ran during the await, this slot is gone or replaced — dispose the
      // freshly-built client instead of stranding it (the Plan-3 sidecar is a real OS process).
      if (this._byKey.get(key)?.token !== token) {
        if (client?.dispose) { try { await client.dispose(); } catch { /* ignore */ } }
        return ROSLYN_STATES.OFF;
      }
      if (!client) {
        this._byKey.set(key, { state: ROSLYN_STATES.UNAVAILABLE, client: null, error: 'Roslyn backend is not installed', token });
        return ROSLYN_STATES.UNAVAILABLE;
      }
      this._byKey.set(key, { state: ROSLYN_STATES.READY, client, token });
      return ROSLYN_STATES.READY;
    } catch (e) {
      if (this._byKey.get(key)?.token === token) {
        this._byKey.set(key, { state: ROSLYN_STATES.UNAVAILABLE, client: null, error: e.message, token });
      }
      return ROSLYN_STATES.UNAVAILABLE;
    }
  }

  async stop(key) {
    const e = this._byKey.get(key);
    if (e?.client?.dispose) { try { await e.client.dispose(); } catch { /* ignore */ } }
    this._byKey.delete(key);
  }
}

// Module singleton the meta-tool handlers use in production (default factory → inert until Plan 3).
export const roslynManager = new RoslynManager();
