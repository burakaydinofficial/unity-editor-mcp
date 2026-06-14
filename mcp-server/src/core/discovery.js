/**
 * Editor instance discovery — the Node mirror of the C# Core's
 * EndpointAddressing + InstanceRegistry (unity-editor-mcp/Core/). Both sides
 * MUST implement identical rules; the FNV-1a vectors and directory layout are
 * pinned by tests on each side (ADR 0003).
 *
 * Each running editor publishes a JSON descriptor
 *   { schemaVersion, projectPath, port, pid, unityVersion, protocolVersion,
 *     startedAt, lastHeartbeat }
 * into a per-user registry directory. The registry is the authoritative
 * endpoint directory; the derived port is only the default the editor tries
 * first (it falls back to an ephemeral port on collision).
 */
import { readdirSync, readFileSync, existsSync, unlinkSync } from 'node:fs';
import { join } from 'node:path';
import { homedir, hostname } from 'node:os';

export const DEFAULT_BASE_PORT = 6400;
export const DEFAULT_PORT_RANGE = 1024;
export const STALE_AFTER_MS = 300_000;
export const REGISTRY_DIR_ENV = 'UNITY_MCP_REGISTRY_DIR';

/**
 * Normalizes a project path so equivalent paths hash identically (mirrors C#
 * EndpointAddressing.Normalize). Case folding is ASCII-only (A–Z → a–z), NOT
 * toLowerCase(): full Unicode special-casing (e.g. U+0130) diverges from .NET's
 * ToLowerInvariant and would silently break port/filename parity.
 */
export function normalizeProjectPath(path) {
  if (!path) return '';
  return path
    .replace(/\\/g, '/')
    .replace(/\/+$/, '')
    .replace(/[A-Z]/g, (c) => String.fromCharCode(c.charCodeAt(0) + 32));
}

/** 32-bit FNV-1a over UTF-16 code units (mirrors C# — NOT over UTF-8 bytes). */
export function fnv1a(value) {
  let hash = 2166136261 >>> 0;
  for (let i = 0; i < value.length; i++) {
    hash = (hash ^ value.charCodeAt(i)) >>> 0;
    hash = Math.imul(hash, 16777619) >>> 0;
  }
  return hash;
}

/** Deterministic per-project default port in [basePort, basePort+range). */
export function derivePort(projectPath, basePort = DEFAULT_BASE_PORT, range = DEFAULT_PORT_RANGE) {
  if (range < 1) range = 1;
  return basePort + (fnv1a(normalizeProjectPath(projectPath)) % range);
}

/** Registry filename for a project (mirrors C# InstanceRegistry.FileNameFor). */
export function instanceFileName(projectPath) {
  return fnv1a(normalizeProjectPath(projectPath)).toString(16).padStart(8, '0') + '.json';
}

/**
 * Default per-user registry directory (mirrors C# InstanceRegistry.DefaultDirectory):
 * 1. UNITY_MCP_REGISTRY_DIR if set;
 * 2. Windows: %LOCALAPPDATA% (else %USERPROFILE%/AppData/Local);
 * 3. macOS: $HOME/Library/Application Support;
 * 4. otherwise: $XDG_DATA_HOME (else $HOME/.local/share);
 * then + /unity-editor-mcp/instances.
 */
export function defaultRegistryDirectory(env = process.env, platform = process.platform) {
  if (env[REGISTRY_DIR_ENV]) return env[REGISTRY_DIR_ENV];

  let baseDir;
  if (platform === 'win32') {
    baseDir = env.LOCALAPPDATA || join(env.USERPROFILE || homedir(), 'AppData', 'Local');
  } else if (platform === 'darwin') {
    baseDir = join(env.HOME || homedir(), 'Library', 'Application Support');
  } else {
    baseDir = env.XDG_DATA_HOME || join(env.HOME || homedir(), '.local', 'share');
  }
  return join(baseDir, 'unity-editor-mcp', 'instances');
}

/** True if the descriptor's heartbeat is within the staleness window. */
export function isFresh(descriptor, nowMs = Date.now(), staleAfterMs = STALE_AFTER_MS) {
  const heartbeat = Date.parse(descriptor?.lastHeartbeat);
  return Number.isFinite(heartbeat) && nowMs - heartbeat <= staleAfterMs;
}

/** True if a process with this id currently exists on this host (mirrors C# IsProcessAlive). */
export function isProcessAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0); // signal 0 = liveness probe, doesn't actually signal
    return true;
  } catch (e) {
    return e.code === 'EPERM'; // exists but not ours -> alive; ESRCH -> dead
  }
}

/**
 * Whether a descriptor represents a live editor — mirrors C# InstanceRegistry.IsLive:
 * on the same host, process liveness is authoritative (collapses the heartbeat-timeout
 * window); for an unknown/other host, fall back to the heartbeat.
 */
export function isLive(descriptor, nowMs = Date.now(), currentHost = hostname(), isAlive = isProcessAlive) {
  if (!descriptor) return false;
  if (descriptor.host && currentHost &&
      String(descriptor.host).toLowerCase() === String(currentHost).toLowerCase()) {
    return isAlive(descriptor.pid);
  }
  return isFresh(descriptor, nowMs);
}

/** All readable descriptors in the registry; corrupt/foreign files are skipped. */
export function readInstances(registryDir) {
  if (!existsSync(registryDir)) return [];
  const instances = [];
  for (const entry of readdirSync(registryDir)) {
    if (!entry.endsWith('.json')) continue;
    try {
      const descriptor = JSON.parse(readFileSync(join(registryDir, entry), 'utf8'));
      if (descriptor && typeof descriptor.projectPath === 'string' && descriptor.projectPath) {
        instances.push(descriptor);
      }
    } catch {
      // Corrupt/partial file — the owning editor rewrites it on heartbeat.
    }
  }
  return instances;
}

/** Deletes descriptors that are no longer live; returns how many were removed. */
export function reapStale(registryDir, nowMs = Date.now(), currentHost = hostname(), isAlive = isProcessAlive) {
  if (!existsSync(registryDir)) return 0;
  let reaped = 0;
  for (const entry of readdirSync(registryDir)) {
    if (!entry.endsWith('.json')) continue;
    const file = join(registryDir, entry);
    let dead;
    try {
      dead = !isLive(JSON.parse(readFileSync(file, 'utf8')), nowMs, currentHost, isAlive);
    } catch {
      dead = true; // corrupt
    }
    if (dead) {
      try { unlinkSync(file); reaped++; } catch { /* held elsewhere — skip */ }
    }
  }
  return reaped;
}

/** The descriptor for a project, or null (payload path checked, mirrors C#). */
export function findInstanceByProjectPath(registryDir, projectPath) {
  const file = join(registryDir, instanceFileName(projectPath));
  if (!existsSync(file)) return null;
  try {
    const descriptor = JSON.parse(readFileSync(file, 'utf8'));
    if (descriptor && normalizeProjectPath(descriptor.projectPath) === normalizeProjectPath(projectPath)) {
      return descriptor;
    }
    return null;
  } catch {
    return null;
  }
}

/**
 * Resolves the Unity port for this server instance:
 * 1. UNITY_PORT (explicit) wins;
 * 2. else with UNITY_PROJECT_PATH: a fresh registry descriptor's actual port,
 *    falling back to the project-derived default port;
 * 3. else the legacy fixed default (6400).
 */
export function resolveUnityPort(env = process.env) {
  const explicit = parseInt(env.UNITY_PORT, 10);
  // A valid TCP target port is 1..65535; ignore 0 (and out-of-range) so an empty/
  // bogus UNITY_PORT falls through to discovery/derivation rather than being used.
  if (Number.isFinite(explicit) && explicit > 0 && explicit < 65536) return explicit;

  const projectPath = env.UNITY_PROJECT_PATH;
  if (projectPath) {
    try {
      const instance = findInstanceByProjectPath(defaultRegistryDirectory(env), projectPath);
      if (instance && isLive(instance) && Number.isFinite(instance.port)) {
        return instance.port;
      }
    } catch {
      // Unreadable registry — fall back to derivation.
    }
    return derivePort(projectPath);
  }
  return DEFAULT_BASE_PORT;
}
