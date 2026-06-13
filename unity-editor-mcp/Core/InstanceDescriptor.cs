using System;
using Newtonsoft.Json;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// The discovery record one editor instance publishes about itself: which
    /// project it serves, where it listens, and whether it is still alive. The
    /// filesystem registry of these descriptors is the authoritative endpoint
    /// directory; the derived port (<see cref="EndpointAddressing"/>) is only the
    /// default the editor *tries* first. See ADR 0003.
    /// </summary>
    public sealed class InstanceDescriptor
    {
        /// <summary>Descriptor schema version (bump on breaking shape changes).</summary>
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = 1;

        /// <summary>Project root (the folder containing Assets/).</summary>
        [JsonProperty("projectPath")] public string ProjectPath { get; set; }

        /// <summary>The port the editor's listener is actually bound to.</summary>
        [JsonProperty("port")] public int Port { get; set; }

        /// <summary>Editor process id, for liveness checks.</summary>
        [JsonProperty("pid")] public int Pid { get; set; }

        /// <summary>Machine name, so PID liveness is only trusted on the same host.</summary>
        [JsonProperty("host")] public string Host { get; set; }

        [JsonProperty("unityVersion")] public string UnityVersion { get; set; }

        [JsonProperty("protocolVersion")] public string ProtocolVersion { get; set; } = CommandCatalog.ProtocolVersion;

        /// <summary>When this instance started (UTC, ISO 8601).</summary>
        [JsonProperty("startedAt")] public DateTime StartedAtUtc { get; set; }

        /// <summary>Last heartbeat (UTC, ISO 8601). Refreshed periodically while alive.</summary>
        [JsonProperty("lastHeartbeat")] public DateTime LastHeartbeatUtc { get; set; }

        /// <summary>True if the heartbeat is within <paramref name="maxAge"/> of <paramref name="nowUtc"/>.</summary>
        public bool IsFresh(DateTime nowUtc, TimeSpan maxAge)
        {
            return nowUtc - LastHeartbeatUtc.ToUniversalTime() <= maxAge;
        }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);

        public static InstanceDescriptor FromJson(string json) =>
            JsonConvert.DeserializeObject<InstanceDescriptor>(json);
    }
}
