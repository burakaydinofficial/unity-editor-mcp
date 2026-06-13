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

/** Normalizes a project path for comparison (mirrors C# EndpointAddressing.Normalize). */
function normalizePath(path) {
  if (!path) return '';
  return path.replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
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
