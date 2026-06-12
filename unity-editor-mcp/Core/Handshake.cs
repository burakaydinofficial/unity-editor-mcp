using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// The connect-time handshake payload: protocol version + editor identity +
    /// the per-editor command availability list. This is what lets the server
    /// detect a protocol or wrong-project connection and advertise only the tools
    /// the connected editor actually supports (requirements B3/C3/C4/G7). A
    /// Unity-independent data carrier — the Unity layer fills UnityVersion,
    /// ProjectPath, and AvailableCommands.
    /// </summary>
    public sealed class Handshake
    {
        public string ProtocolVersion { get; set; } = CommandCatalog.ProtocolVersion;
        public string UnityVersion { get; set; }
        public string ProjectPath { get; set; }
        public IReadOnlyList<string> AvailableCommands { get; set; } = Array.Empty<string>();

        public string ToJson()
        {
            var commands = new JArray();
            foreach (var c in AvailableCommands ?? Array.Empty<string>()) commands.Add(c);
            var o = new JObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["unityVersion"] = UnityVersion,
                ["projectPath"] = ProjectPath,
                ["availableCommands"] = commands,
            };
            return o.ToString(Formatting.None);
        }

        public static Handshake FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("empty handshake json", nameof(json));
            var o = JObject.Parse(json);
            return new Handshake
            {
                ProtocolVersion = (string)o["protocolVersion"],
                UnityVersion = (string)o["unityVersion"],
                ProjectPath = (string)o["projectPath"],
                AvailableCommands = o["availableCommands"] is JArray arr
                    ? arr.Select(t => (string)t).Where(s => s != null).ToList()
                    : new List<string>(),
            };
        }

        /// <summary>
        /// Checks this (remote) handshake against the local protocol version and,
        /// optionally, an expected project path. Returns a structured result the
        /// server uses to refuse a mismatched protocol or a wrong-project editor.
        /// </summary>
        public CompatibilityResult CheckAgainst(string localProtocolVersion, string expectedProjectPath = null)
        {
            var version = ProtocolCompatibility.Check(localProtocolVersion, ProtocolVersion);
            if (!version.Compatible) return version;

            if (!string.IsNullOrEmpty(expectedProjectPath) && !PathsEqual(expectedProjectPath, ProjectPath))
            {
                return new CompatibilityResult(false, "PROJECT_PATH_MISMATCH",
                    $"Connected editor project '{ProjectPath}' does not match the targeted '{expectedProjectPath}'.");
            }

            return version;
        }

        private static bool PathsEqual(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
    }
}
