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
        /// <summary>True if <paramref name="candidatePath"/> (resolved absolutely) stays within the project root.
        /// Delegates to the dotnet-tested Core.PathContainment; the project root is Application.dataPath/.. .</summary>
        public static bool IsWithinProject(string candidatePath)
            => Core.PathContainment.IsWithin(Application.dataPath + "/..", candidatePath);

        /// <summary>Returns a VALIDATION_ERROR outcome if <paramref name="path"/> is non-empty and escapes the
        /// project root; null when the path is empty (the caller validates required-ness) or contained. (H4)</summary>
        public static Core.HandlerOutcome Guard(string path, string label = "path")
            => (!string.IsNullOrEmpty(path) && !IsWithinProject(path))
                ? Core.HandlerOutcome.Fail($"{label} must stay within the project root", "VALIDATION_ERROR")
                : null;
    }
}
