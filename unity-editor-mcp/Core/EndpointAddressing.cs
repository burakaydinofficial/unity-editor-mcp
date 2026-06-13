namespace UnityEditorMCP.Core
{
    /// <summary>
    /// Derives a deterministic, per-project TCP port so concurrent Unity editors
    /// do not collide on a single fixed port (requirement C3). The same algorithm
    /// must run identically on the Node server, so it uses FNV-1a over UTF-16 code
    /// units — NOT <c>string.GetHashCode</c>, which is randomized per process and
    /// would differ between runs and between the two halves. The derived port is
    /// only the default; the authoritative endpoint is published in a discovery
    /// descriptor (so an actual collision can fall back to an ephemeral port).
    /// </summary>
    public static class EndpointAddressing
    {
        /// <summary>Base of the derived port range.</summary>
        public const int DefaultBasePort = 6400;

        /// <summary>Size of the derived port range (6400–7423 by default).</summary>
        public const int DefaultRange = 1024;

        /// <summary>Deterministic port for a project path, in [basePort, basePort+range).</summary>
        public static int DerivePort(string projectPath, int basePort = DefaultBasePort, int range = DefaultRange)
        {
            if (range < 1) range = 1;
            uint hash = Fnv1a(Normalize(projectPath));
            return basePort + (int)(hash % (uint)range);
        }

        /// <summary>
        /// Normalizes a project path so equivalent paths derive the same port.
        /// Case folding is ASCII-only (A–Z → a–z) and deliberately NOT
        /// <c>ToLowerInvariant</c>: full Unicode special-casing diverges between C#
        /// and JS (e.g. U+0130 'İ' lowercases to two code points in JS but stays one
        /// in .NET), which would silently break port/filename parity. All non-ASCII
        /// code points are left byte-identical on both sides.
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var chars = path.Replace('\\', '/').TrimEnd('/').ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c >= 'A' && c <= 'Z') chars[i] = (char)(c + 32);
            }
            return new string(chars);
        }

        /// <summary>32-bit FNV-1a hash over UTF-16 code units (stable across processes and languages).</summary>
        public static uint Fnv1a(string value)
        {
            const uint offsetBasis = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offsetBasis;
            if (value != null)
            {
                foreach (char c in value)
                {
                    hash ^= c;
                    hash *= prime;
                }
            }
            return hash;
        }
    }
}
