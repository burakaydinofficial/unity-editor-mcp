using System;
using System.IO;
using UnityEngine;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Path-traversal containment for caller-supplied file paths. A caller — via the generic
    /// call_unity_tool relay or a direct TCP client — must not be able to read or write OUTSIDE the
    /// Unity project root through a `..` segment. Added in v0.5.0 after the audit found create_script /
    /// analyze_screenshot, and then their sibling handlers (read/update/delete/validate_script,
    /// capture_screenshot), resolved unchecked paths. Note: with the typed Node passthrough handlers
    /// deleted in v0.5.0, the editor side is the SOLE guard for those commands.
    /// </summary>
    internal static class PathSafety
    {
        /// <summary>True if <paramref name="candidatePath"/> (resolved absolutely) stays within the project root.</summary>
        public static bool IsWithinProject(string candidatePath)
        {
            if (string.IsNullOrEmpty(candidatePath)) return false;
            string projectRoot = Path.GetFullPath(Application.dataPath + "/..");
            string rootWithSep = projectRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? projectRoot
                : projectRoot + Path.DirectorySeparatorChar;
            string full;
            try
            {
                // Resolve a relative path against the PROJECT ROOT explicitly (an absolute path is taken
                // as-is). Don't rely on the process CWD — Unity sets it to the project root, but making
                // the base explicit keeps the guard correct regardless.
                full = Path.GetFullPath(Path.IsPathRooted(candidatePath)
                    ? candidatePath
                    : Path.Combine(projectRoot, candidatePath));
            }
            catch { return false; }
            // Strictly under the root (the trailing separator blocks a sibling-prefix collision like
            // "C:/proj" vs "C:/proj-evil"), or the root itself.
            return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, projectRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
