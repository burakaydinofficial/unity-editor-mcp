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
        public static bool IsWithin(string root, string candidate)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate)) return false;
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
            return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, projectRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
