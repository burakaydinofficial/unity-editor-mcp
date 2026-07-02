using System;
using System.IO;

namespace UnityEditorMCP.Core
{
    /// <summary>Pure path-traversal containment (H4) — Unity-independent so the security-critical logic is
    /// dotnet-tested. A candidate path (resolved absolutely against <paramref name="root"/>) must stay strictly
    /// under the root, with a trailing separator to block sibling-prefix collisions ("proj" vs "proj-evil").
    /// Canonicalizes via GetFullPath (collapses ".."); does NOT resolve symlinks/junctions — an accepted
    /// Mono/2020.3 floor limitation (see the H4 design + PathSafety).</summary>
    public static class PathContainment
    {
        // Case-insensitive on Windows/macOS (their filesystems fold case); case-SENSITIVE on Linux — where the
        // floor-matrix CI runs the EditMode suite — so a case-variant sibling ("proj" vs "PROJ/secret") is correctly
        // denied there instead of over-accepted. (Bug hunt: security.)
        private static readonly StringComparison PathComparison =
            (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
             || System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static bool IsWithin(string root, string candidate)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate)) return false;

            // DRIVE-RELATIVE paths ("C:foo", "C:..\x") are "rooted" per IsPathRooted but resolve against the
            // process's per-drive CWD — not the project root — violating the never-resolve-against-CWD invariant.
            // Reject them outright. (Bug hunt Sec-3.)
            if (candidate.Length >= 2 && candidate[1] == ':' &&
                (candidate.Length == 2 || (candidate[2] != '\\' && candidate[2] != '/')))
                return false;

            string projectRoot;
            try { projectRoot = Path.GetFullPath(root); } catch { return false; }
            string rootWithSep = projectRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? projectRoot
                : projectRoot + Path.DirectorySeparatorChar;
            string full;
            try
            {
                // Resolve a relative candidate against the ROOT explicitly (an absolute path is taken as-is) —
                // never the process CWD.
                full = Path.GetFullPath(Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.Combine(projectRoot, candidate));
            }
            catch { return false; }

            // NTFS alternate-data-stream colon in the final segment ("Assets/Foo.cs:stream"): runtime-divergent
            // (older Mono throws in GetFullPath -> denied; newer .NET accepts -> a hidden stream AssetDatabase
            // won't track). Make the denial deterministic. (Bug hunt Sec-6.)
            var fileName = Path.GetFileName(full);
            if (fileName != null && fileName.IndexOf(':') >= 0) return false;

            return full.StartsWith(rootWithSep, PathComparison)
                || string.Equals(full, projectRoot, PathComparison);
        }
    }
}
