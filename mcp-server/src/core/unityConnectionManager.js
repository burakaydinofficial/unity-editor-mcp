import { config, logger } from './config.js';
import { UnityConnection } from './unityConnection.js';
import { performHandshake } from './handshake.js';
import * as discovery from './discovery.js';

/**
 * Manages a pool of UnityConnections — one per editor instance — so a single MCP server can drive
 * several Unity editors concurrently (ADR 0005, "one server -> many editors"). Every connection is a
 * PINNED instance keyed by host:port, opened lazily on first use; there is NO default/active
 * connection (ADR 0006 — every call names its instance). Each connection handshakes on connect and
 * caches its editor manifest on `conn.editorInfo`.
 *
 * The connection factory, handshake, discovery, env, and host are all injectable so the manager is
 * unit-tested deterministically with neither real sockets nor a real registry.
 */
export class UnityConnectionManager {
  constructor(options = {}) {
    this.createConnection = options.createConnection || ((opts) => new UnityConnection(opts));
    this.performHandshake = options.performHandshake || performHandshake;
    this.discovery = options.discovery || discovery;
    this.env = options.env || process.env;
    this.host = options.host || config.unity.host;
    this.connections = new Map(); // key "host:port" -> pinned UnityConnection
  }

  key(host, port) {
    return `${host}:${port}`;
  }

  /** Attaches a handshake-on-connect handler that caches the editor manifest on the connection. */
  wireHandshake(conn, k) {
    if (typeof conn.on !== 'function') return conn;
    // Every managed connection MUST have an 'error' listener: a socket error on an EventEmitter with
    // no 'error' listener throws and crashes the process. server.js attaches one to the active
    // connection, but pooled instance connections had none. (Audit finding.)
    conn.on('error', (e) => logger.error(`[Manager ${k}] connection error: ${e && e.message}`));
    conn.on('connected', () => {
      // Clear any manifest from a previous session BEFORE re-handshaking, so a failed reconnect
      // handshake never leaves a stale/phantom manifest in place. (Audit finding.)
      conn.editorInfo = null;
      // Store the in-flight handshake so ensureReady() can await manifest readiness. The handler
      // runs synchronously during emit('connected') (before connect() resolves), so the promise is
      // set by the time the caller's `await connect()` returns.
      conn.handshakePromise = (async () => {
        try {
          // Every connection is a pinned instance targeted explicitly, so skip the env project-path check.
          const r = await this.performHandshake(conn, { expectedProjectPath: null });
          conn.editorInfo = r.handshake ?? null;
          if (r.performed && !r.compatible) logger.warn(`[Manager ${k}] ${r.code}: ${r.message}`);
          return r;
        } catch (e) {
          conn.editorInfo = null; // failed handshake -> no manifest (don't serve a phantom one)
          logger.debug(`[Manager ${k}] handshake error: ${e.message}`);
          return null;
        }
      })();
    });
    return conn;
  }

  /** Connects the connection if needed and awaits its handshake, so conn.editorInfo is populated. */
  async ensureReady(conn) {
    if (typeof conn.isConnected === 'function' && !conn.isConnected()) {
      await conn.connect();
    }
    if (conn.handshakePromise) {
      try { await conn.handshakePromise; } catch { /* handshake failures are non-fatal */ }
    }
    return conn;
  }

  /** Lazily returns the PINNED connection for a host:port. */
  getConnection(host, port) {
    const k = this.key(host, port);
    const existing = this.connections.get(k);
    if (existing) return existing;
    const conn = this.wireHandshake(this.createConnection({ host, port }), k);
    this.connections.set(k, conn);
    return conn;
  }

  /**
   * Resolves an instance reference to {host, port}, or null if unresolved.
   * - null/empty -> null (there is no default instance — ADR 0006);
   * - a port number (or numeric string) -> that port on the default host;
   * - any other string -> a project path looked up in the registry.
   */
  resolveInstance(ref) {
    if (ref == null || ref === '') return null;
    const asPort = Number(ref);
    if (Number.isInteger(asPort) && asPort > 0 && asPort < 65536) {
      return { host: this.host, port: asPort };
    }
    try {
      const dir = this.discovery.defaultRegistryDirectory(this.env);
      const desc = this.discovery.findInstanceByProjectPath(dir, String(ref));
      // desc.host is a machine IDENTITY from the local registry, not a connect address: same-host editors bind
      // loopback, so always connect via the configured host (matches the port-ref path AND the pooled key space).
      // Require the descriptor to be live so a stale dead-editor port isn't resolved into a 30s stall. (Node-1/2/6.)
      if (desc && Number.isFinite(desc.port) && this.discovery.isLive(desc)) return { host: this.host, port: desc.port };
    } catch { /* unreadable registry -> unresolved */ }
    return null;
  }

  /**
   * Resolves the connection for an EXPLICIT instance ref, throwing a clear error if the ref is missing
   * or unresolved. There is NO default instance (ADR 0006): every instance-bound call must name its
   * editor, so a forgotten/wrong target fails loudly instead of silently acting on the wrong project.
   */
  requireConnection(ref) {
    if (ref == null || (typeof ref === 'string' && ref.trim() === '')) {
      throw new Error('instance is required: name the target editor (a project path or port). There is no default instance — every call must name its editor so an agent never acts on the wrong project. Use list_unity_instances to see what is running.');
    }
    const t = this.resolveInstance(ref);
    if (!t) {
      throw new Error(`No Unity instance found for "${ref}". Use list_unity_instances to see what is running.`);
    }
    return this.getConnection(t.host, t.port);
  }

  /** Closes + drops connections whose editor is no longer live in the registry. */
  prune() {
    let live;
    try {
      const dir = this.discovery.defaultRegistryDirectory(this.env);
      live = new Set(
        this.discovery.readInstances(dir)
          .filter((d) => this.discovery.isLive(d))
          // Key on the CONFIGURED host, matching getConnection() + port-refs — NOT desc.host (machine name), which
          // would never match a port-targeted connection's `localhost:port` key and prune it on every call. (Node-2.)
          .map((d) => this.key(this.host, d.port)),
      );
    } catch {
      return 0;
    }
    let pruned = 0;
    for (const [k, conn] of this.connections) {
      if (!live.has(k)) {
        try { conn.disconnect(); } catch { /* ignore */ }
        this.connections.delete(k);
        pruned++;
      }
    }
    return pruned;
  }

  listConnections() {
    return [...this.connections.entries()].map(([k, conn]) => ({
      key: k,
      connected: typeof conn.isConnected === 'function' ? conn.isConnected() : false,
      editorInfo: conn.editorInfo || null,
    }));
  }

  disconnectAll() {
    for (const conn of this.connections.values()) {
      try { conn.disconnect(); } catch { /* ignore */ }
    }
    this.connections.clear();
  }
}
