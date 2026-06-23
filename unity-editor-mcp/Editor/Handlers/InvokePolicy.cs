using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// H2 allow/deny policy for static-method invocation (invoke_static_method). DEFAULT-DENY: a static invoke is
    /// rejected unless its "FullType.Method" matches an allow pattern from either the UNITY_MCP_INVOKE_ALLOW env
    /// var (comma/semicolon-separated) or ProjectSettings/UnityEditorMcpInvokePolicy.json ({ "allowInvoke": [...] }).
    /// Patterns: exact "Ns.Type.Method", prefix "Ns.Type.*" (any method of the type), or "*" (allow all — full ACE).
    /// Read fresh per call (infrequent op): policy edits take effect immediately, and tests can set the env var.
    /// </summary>
    internal static class InvokePolicy
    {
        public static bool IsAllowed(string typeName, string methodName)
        {
            var target = (typeName ?? "") + "." + (methodName ?? "");
            foreach (var pattern in LoadPatterns())
                if (Matches(pattern, target)) return true;
            return false;
        }

        private static IEnumerable<string> LoadPatterns()
        {
            var env = Environment.GetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW");
            if (!string.IsNullOrEmpty(env))
                foreach (var p in env.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    yield return p.Trim();

            JArray arr = null;
            var json = ReadConfigFile();
            if (json != null)
            {
                try { arr = JObject.Parse(json)["allowInvoke"] as JArray; }
                catch { arr = null; /* malformed config -> contributes no patterns (stays default-deny) */ }
            }
            if (arr != null)
                foreach (var t in arr)
                {
                    var s = t?.ToString();
                    if (!string.IsNullOrEmpty(s)) yield return s.Trim();
                }
        }

        private static string ReadConfigFile()
        {
            try
            {
                var root = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(root)) return null;
                var path = Path.Combine(root, "ProjectSettings", "UnityEditorMcpInvokePolicy.json");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch { return null; }
        }

        private static bool Matches(string pattern, string target)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern == "*") return true;
            if (pattern.EndsWith(".*"))
                return target.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal);
            return string.Equals(pattern, target, StringComparison.Ordinal);
        }
    }
}
