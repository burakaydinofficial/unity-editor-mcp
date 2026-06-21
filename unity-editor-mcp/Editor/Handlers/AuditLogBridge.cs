using System;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>Editor-side wiring for the H5 audit log: resolves the Library path, honors the env toggle,
    /// extracts a target hint, and delegates to the Unity-independent Core AuditLog (fail-safe).</summary>
    public static class AuditLogBridge
    {
        private static string _path;
        public static string Path => _path ?? (_path = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(Application.dataPath + "/.."), "Library", "UnityEditorMCP", "audit-log.jsonl"));

        // On by default; UNITY_MCP_AUDIT_LOG=0 disables.
        private static bool Enabled => Environment.GetEnvironmentVariable("UNITY_MCP_AUDIT_LOG") != "0";

        public static void Record(string type, JObject parameters, bool ok)
        {
            if (!Enabled || string.IsNullOrEmpty(type)) return;
            AuditLog.Append(Path, type, TargetHint(parameters), ok);
        }

        private static string TargetHint(JObject p)
        {
            if (p == null) return "";
            foreach (var k in new[] { "assetPath", "gameObjectPath", "path", "scenePath", "prefabPath", "variantPath", "typeName", "scriptName", "name" })
                if (p[k] != null) return p[k].ToString();
            if (p["target"] is JObject t)
                foreach (var k in new[] { "scenePath", "assetPath", "guid", "instanceId" })
                    if (t[k] != null) return t[k].ToString();
            return "";
        }
    }
}
