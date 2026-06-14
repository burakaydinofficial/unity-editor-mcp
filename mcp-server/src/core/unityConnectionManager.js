import { config, logger } from './config.js';
import { UnityConnection } from './unityConnection.js';
import { performHandshake } from './handshake.js';
import * as discovery from './discovery.js';

/**
 * Manages a pool of UnityConnections — one per editor instance — so a single MCP server can drive
 * several Unity editors concurrently (ADR 0005, "one server -> many editors"). Connections are
 * lazily opened per target and keyed by host:port; each handshakes on connect and caches its
 * editor manifest on `conn.editorInfo`. The "active" instance is the default target for calls that
 * don't name one (env/registry-resolved by default; overridable via setActiveInstance).
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
    this.connections = new Map(); // "host:port" -> UnityConnection
    this.activeOverride = null;   // { host, port } | null
  }

  key(host, port) {
    return `${host}:${port}`;
  }

  /** Lazily returns the connection for a host:port, wiring a handshake-on-connect that caches the manifest. */
  getConnection(host, port) {
    const k = this.key(host, port);
    const existing = this.connections.get(k);
    if (existing) return existing;

    const conn = this.createConnection({ host, port });
    if (typeof conn.on === 'function') {
      conn.on('connected', async () => {
        try {
          const r = await this.performHandshake(conn);
          conn.editorInfo = r.handshake || null;
          if (r.performed && !r.compatible) logger.warn(`[Manager ${k}] ${r.code}: ${r.message}`);
        } catch (e) {
          logger.debug(`[Manager ${k}] handshake error: ${e.message}`);
        }
      });
    }
    this.connections.set(k, conn);
    return conn;
  }

  /** The default target: an explicit override, else the env/registry-resolved port. */
  activeTarget() {
    if (this.activeOverride) return this.activeOverride;
    let port;
    try { port = this.discovery.resolveUnityPort(this.env); } catch { port = config.unity.port; }
    return { host: this.host, port };
  }

  getActiveConnection() {
    const { host, port } = this.activeTarget();
    return this.getConnection(host, port);
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

  getConnectionForInstance(ref) {
    const t = this.resolveInstance(ref);
    return t ? this.getConnection(t.host, t.port) : null;
  }

  /** Sets the default target. Returns the resolved {host, port}, or null if unresolved (override unchanged). */
  setActiveInstance(ref) {
    const t = this.resolveInstance(ref);
    if (t) this.activeOverride = t;
    return t;
  }

  /** Closes + drops connections whose editor is no longer live in the registry. Returns the count pruned. */
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
