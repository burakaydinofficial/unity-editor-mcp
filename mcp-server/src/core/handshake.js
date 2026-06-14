/**
 * Connect-time handshake evaluation — the Node mirror of the C# Core
 * ProtocolCompatibility + Handshake.CheckAgainst (unity-editor-mcp/Core/). The
 * editor answers a `handshake` command with { protocolVersion, unityVersion,
 * projectPath, availableCommands }; the server checks it here and warns (or, in
 * future, refuses) on a protocol-major or wrong-project mismatch.
 *
 * Compatibility rule: peers are compatible iff they share a protocol major; a
 * minor skew within a major is tolerated.
 */

// Must match protocol/VERSION (verified by protocol/scripts/check-drift.mjs).
export const PROTOCOL_VERSION = '1.0.0';

/** Parses "MAJOR.MINOR.PATCH"; returns null if malformed. */
export function parseSemver(version) {
  if (typeof version !== 'string') return null;
  const match = version.trim().match(/^(\d+)\.(\d+)\.(\d+)$/);
  if (!match) return null;
  return { major: Number(match[1]), minor: Number(match[2]), patch: Number(match[3]) };
}

/** Compares two protocol versions; mirrors C# ProtocolCompatibility.Check. */
export function checkProtocolCompatibility(localVersion, remoteVersion) {
  const local = parseSemver(localVersion);
  const remote = parseSemver(remoteVersion);

  if (!local || !remote) {
    return {
      compatible: false,
      code: 'PROTOCOL_VERSION_MISMATCH',
      message: `Unparseable protocol version (local '${localVersion}', remote '${remoteVersion}').`,
    };
  }

  if (local.major !== remote.major) {
    return {
      compatible: false,
      code: 'PROTOCOL_VERSION_MISMATCH',
      message: `Protocol major mismatch (local ${localVersion}, remote ${remoteVersion}). ` +
        'Update whichever package is older so both share a protocol major.',
    };
  }

  return {
    compatible: true,
    code: null,
    message: local.minor === remote.minor
      ? `Protocol ${localVersion} matches.`
      : `Protocol minor skew (local ${localVersion}, remote ${remoteVersion}); compatible using the lower minor's surface.`,
  };
}

/**
 * Normalizes a project path for the PROJECT_PATH_MISMATCH check. Mirrors C#
 * Handshake.Normalize (separator + trailing-slash) plus the OrdinalIgnoreCase
 * comparison it uses — folded here as ASCII-only (same as discovery.js
 * normalizeProjectPath), so non-ASCII paths don't diverge the way JS
 * .toLowerCase() (full-Unicode, locale-sensitive) would.
 */
function normalizePath(path) {
  if (!path) return '';
  return path
    .replace(/\\/g, '/')
    .replace(/\/+$/, '')
    .replace(/[A-Z]/g, (c) => String.fromCharCode(c.charCodeAt(0) + 32));
}

/**
 * Evaluates a handshake payload against the local protocol version and, when
 * provided, an expected project path. Mirrors C# Handshake.CheckAgainst.
 * @returns {{compatible: boolean, code: string|null, message: string}}
 */
export function evaluateHandshake(handshake, options = {}) {
  const localProtocolVersion = options.localProtocolVersion || PROTOCOL_VERSION;
  const expectedProjectPath = options.expectedProjectPath || null;

  if (!handshake || typeof handshake !== 'object') {
    return { compatible: false, code: 'PROTOCOL_VERSION_MISMATCH', message: 'No handshake payload received.' };
  }

  const versionResult = checkProtocolCompatibility(localProtocolVersion, handshake.protocolVersion);
  if (!versionResult.compatible) return versionResult;

  if (expectedProjectPath && normalizePath(expectedProjectPath) !== normalizePath(handshake.projectPath)) {
    return {
      compatible: false,
      code: 'PROJECT_PATH_MISMATCH',
      message: `Connected editor project '${handshake.projectPath}' does not match the targeted '${expectedProjectPath}'.`,
    };
  }

  return versionResult;
}

/**
 * Performs the connect-time handshake against a connected editor: sends the
 * `handshake` command, evaluates the reply, and returns a resilient result that
 * NEVER throws — an editor predating the command (UNKNOWN_COMMAND) or any I/O
 * failure degrades to a non-fatal `performed:false`, so the connection is never
 * broken by the handshake itself.
 * @returns {Promise<{performed:boolean, reason?:string, compatible?:boolean,
 *   code?:string|null, message:string, handshake?:object}>}
 */
export async function performHandshake(connection, options = {}) {
  const expectedProjectPath = options.expectedProjectPath
    ?? (typeof process !== 'undefined' ? process.env.UNITY_PROJECT_PATH : null)
    ?? null;
  const localProtocolVersion = options.localProtocolVersion || PROTOCOL_VERSION;

  let handshake;
  try {
    handshake = await connection.sendCommand('handshake');
  } catch (err) {
    if (err && err.code === 'UNKNOWN_COMMAND') {
      return { performed: false, reason: 'unsupported', message: 'Editor predates the handshake command.' };
    }
    return { performed: false, reason: 'error', message: err ? err.message : 'handshake failed' };
  }

  const verdict = evaluateHandshake(handshake, { expectedProjectPath, localProtocolVersion });
  return { performed: true, handshake, ...verdict };
}
