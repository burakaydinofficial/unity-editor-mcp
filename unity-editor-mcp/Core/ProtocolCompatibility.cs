using System;

namespace UnityEditorMCP.Core
{
    /// <summary>Outcome of comparing two protocol versions.</summary>
    public readonly struct CompatibilityResult
    {
        public bool Compatible { get; }
        /// <summary>Machine code when incompatible (else null).</summary>
        public string Code { get; }
        public string Message { get; }

        public CompatibilityResult(bool compatible, string code, string message)
        {
            Compatible = compatible;
            Code = code;
            Message = message;
        }
    }

    /// <summary>
    /// Protocol version negotiation. Two peers are compatible iff they share a
    /// major version; a minor skew within a major is tolerated (the newer side is
    /// expected to restrict itself to the lower minor's surface). Pure logic,
    /// unit-tested without Unity. See protocol/README.md → Versioning.
    /// </summary>
    public static class ProtocolCompatibility
    {
        public static bool TryParse(string version, out int major, out int minor, out int patch)
        {
            major = 0;
            minor = 0;
            patch = 0;
            if (string.IsNullOrWhiteSpace(version)) return false;
            var parts = version.Trim().Split('.');
            if (parts.Length != 3) return false;
            return int.TryParse(parts[0], out major)
                && int.TryParse(parts[1], out minor)
                && int.TryParse(parts[2], out patch);
        }

        public static CompatibilityResult Check(string localVersion, string remoteVersion)
        {
            if (!TryParse(localVersion, out var lMajor, out var lMinor, out _) ||
                !TryParse(remoteVersion, out var rMajor, out var rMinor, out _))
            {
                return new CompatibilityResult(false, "PROTOCOL_VERSION_MISMATCH",
                    $"Unparseable protocol version (local '{localVersion}', remote '{remoteVersion}').");
            }

            if (lMajor != rMajor)
            {
                return new CompatibilityResult(false, "PROTOCOL_VERSION_MISMATCH",
                    $"Protocol major mismatch (local {localVersion}, remote {remoteVersion}). " +
                    "Update whichever package is older so both share a protocol major.");
            }

            var message = lMinor == rMinor
                ? $"Protocol {localVersion} matches."
                : $"Protocol minor skew (local {localVersion}, remote {remoteVersion}); compatible using the lower minor's surface.";
            return new CompatibilityResult(true, null, message);
        }
    }
}
