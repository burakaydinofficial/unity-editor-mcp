import { config, logger } from './config.js';
import { UnityConnection } from './unityConnection.js';
import { performHandshake } from './handshake.js';
import * as discovery from './discovery.js';

// Reserved key for the default connection. It env-resolves its port on every connect (so a
// restarted editor on a new ephemeral port is re-discovered — the v0.2.0 behavior) and is never
// pruned. Explicit per-instance connections, by contrast, are pinned to a fixed host:port.
const ACTIVE_KEY = '__active__';

/**
 * Manages a pool of UnityConnections — one per editor instance — so a single MCP server can drive
 * several Unity editors concurrently (ADR 0005, "one server -> many editors"). The default ("active")
 * connection env-resolves its target; explicit instance connections are pinned and keyed by host:port.
 * Each connection handshakes on connect and caches its editor manifest on `conn.editorInfo`.
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
    this.connections = new Map(); // key -> UnityConnection ("__active__" or "host:port")
    this.activeOverride = null;   // { host, port } | null
  }

  key(host, port) {
    return `${host}:${port}`;
  }

  /** Attaches a handshake-on-connect handler that caches the editor manifest on the connection. */
  wireHandshake(conn, k) {
    if (typeof conn.on !== 'function') return conn;
    conn.on('connected', () => {
      // Store the in-flight handshake so ensureReady() can await manifest readiness. The handler
      // runs synchronously during emit('connected') (before connect() resolves), so the promise is
      // set by the time the caller's `await connect()` returns.
      conn.handshakePromise = (async () => {
        try {
          const r = await this.performHandshake(conn);
          conn.editorInfo = r.handshake || null;
          if (r.performed && !r.compatible) logger.warn(`[Manager ${k}] ${r.code}: ${r.message}`);
          return r;
        } catch (e) {
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
   * The default connection. With an explicit active override it is the pinned connection for that
   * instance; otherwise a single env-resolving connection (re-resolves its port each connect).
   */
  getActiveConnection() {
    if (this.activeOverride) return this.getConnection(this.activeOverride.host, this.activeOverride.port);
    const existing = this.connections.get(ACTIVE_KEY);
    if (existing) return existing;
    // No target port -> UnityConnection.resolveTargetPort() env-resolves on each connect.
    const conn = this.wireHandshake(this.createConnection({ host: this.host }), ACTIVE_KEY);
    this.connections.set(ACTIVE_KEY, conn);
    return conn;
  }

  /** The default target {host, port} (override, else env/registry-resolved). For display/diagnostics. */
  activeTarget() {
    if (this.activeOverride) return this.activeOverride;
    let port;
    try { port = this.discovery.resolveUnityPort(this.env); } catch { port = config.unity.port; }
    return { host: this.host, port };
  }

  /**
   * Resolves an instance reference to {host, port}, or null if unresolved.
   * - null/empty -> the active target;
   * - a port number (or numeric string) -> that port on the default host;
   * - any other string -> a project path looked up in the registry.
   */
  resolveInstance(ref) {
    if (ref == null || ref === '') return this.activeTarget();
    const asPort = Number(ref);
    if (Number.isInteger(asPort) && asPort > 0 && asPort < 65536) {
      return { host: this.host, port: asPort };
    }
    try {
      const dir = this.discovery.defaultRegistryDirectory(this.env);
      const desc = this.discovery.findInstanceByProjectPath(dir, String(ref));
      if (desc && Number.isFinite(desc.port)) return { host: desc.host || this.host, port: desc.port };
    } catch { /* unreadable registry -> unresolved */ }
    return null;
  }

  /** The connection for an instance ref (the active default when ref is null/empty), or null if unresolved. */
  getConnectionForInstance(ref) {
    if (ref == null || ref === '') return this.getActiveConnection();
    const t = this.resolveInstance(ref);
    return t ? this.getConnection(t.host, t.port) : null;
  }

  /** Sets the default target. null/empty clears the override (back to env-resolving). Returns {host,port}|null. */
  setActiveInstance(ref) {
    if (ref == null || ref === '') { this.activeOverride = null; return null; }
    const t = this.resolveInstance(ref);
    if (t) this.activeOverride = t;
    return t;
  }

  /** Closes + drops PINNED connections whose editor is no longer live in the registry (the active default is kept). */
  prune() {
    let live;
    try {
      const dir = this.discovery.defaultRegistryDirectory(this.env);
      live = new Set(
        this.discovery.readInstances(dir)
          .filter((d) => this.discovery.isLive(d))
          .map((d) => this.key(d.host || this.host, d.port)),
      );
    } catch {
      return 0;
    }
    let pruned = 0;
    for (const [k, conn] of this.connections) {
      if (k === ACTIVE_KEY) continue; // the env-resolving default is never pruned
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
