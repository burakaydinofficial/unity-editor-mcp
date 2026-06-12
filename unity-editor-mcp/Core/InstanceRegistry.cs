using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// Filesystem-based instance registry: each running editor publishes an
    /// <see cref="InstanceDescriptor"/> as a JSON file in a per-user directory, so
    /// any client can enumerate running instances and resolve a project to its
    /// actual port — no coordinator process, no leader election, no well-known
    /// socket (ADR 0003). The Node server implements the same directory and
    /// filename rules (mcp-server/src/core/discovery.js); keep them in sync.
    /// </summary>
    public sealed class InstanceRegistry
    {
        /// <summary>Heartbeats older than this mark a descriptor stale.</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(300);

        /// <summary>Environment variable overriding the registry directory.</summary>
        public const string DirectoryEnvVar = "UNITY_MCP_REGISTRY_DIR";

        private readonly string _directory;

        public InstanceRegistry(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("directory is required", nameof(directory));
            _directory = directory;
        }

        /// <summary>The directory this registry reads and writes.</summary>
        public string Directory => _directory;

        /// <summary>
        /// Resolves the default per-user registry directory. Mirrored by the Node
        /// side — the rules must stay identical:
        /// 1. UNITY_MCP_REGISTRY_DIR if set;
        /// 2. Windows: %LOCALAPPDATA% (else %USERPROFILE%/AppData/Local);
        /// 3. macOS: $HOME/Library/Application Support;
        /// 4. otherwise: $XDG_DATA_HOME (else $HOME/.local/share);
        /// then + /unity-editor-mcp/instances.
        /// </summary>
        public static string DefaultDirectory()
        {
            var env = Environment.GetEnvironmentVariable(DirectoryEnvVar);
            if (!string.IsNullOrEmpty(env)) return env;

            string baseDir;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrEmpty(baseDir))
                {
                    baseDir = Path.Combine(
                        Environment.GetEnvironmentVariable("USERPROFILE") ?? ".",
                        "AppData", "Local");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                baseDir = Path.Combine(
                    Environment.GetEnvironmentVariable("HOME") ?? ".",
                    "Library", "Application Support");
            }
            else
            {
                baseDir = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrEmpty(baseDir))
                {
                    baseDir = Path.Combine(
                        Environment.GetEnvironmentVariable("HOME") ?? ".",
                        ".local", "share");
                }
            }
            return Path.Combine(baseDir, "unity-editor-mcp", "instances");
        }

        /// <summary>Registry filename for a project: fnv1a hex of the normalized path.</summary>
        public static string FileNameFor(string projectPath)
        {
            uint hash = EndpointAddressing.Fnv1a(EndpointAddressing.Normalize(projectPath));
            return hash.ToString("x8") + ".json";
        }

        /// <summary>Writes (or overwrites) the descriptor for its project.</summary>
        public void Publish(InstanceDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrEmpty(descriptor.ProjectPath))
                throw new ArgumentException("descriptor.ProjectPath is required", nameof(descriptor));

            System.IO.Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, FileNameFor(descriptor.ProjectPath));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, descriptor.ToJson());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Removes the descriptor for a project (no-op if absent).</summary>
        public void Remove(string projectPath)
        {
            var path = Path.Combine(_directory, FileNameFor(projectPath));
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>All readable descriptors; corrupt or foreign files are skipped.</summary>
        public IReadOnlyList<InstanceDescriptor> ReadAll()
        {
            var result = new List<InstanceDescriptor>();
            if (!System.IO.Directory.Exists(_directory)) return result;
            foreach (var file in System.IO.Directory.GetFiles(_directory, "*.json"))
            {
                try
                {
                    var descriptor = InstanceDescriptor.FromJson(File.ReadAllText(file));
                    if (descriptor != null && !string.IsNullOrEmpty(descriptor.ProjectPath)) result.Add(descriptor);
                }
                catch
                {
                    // Corrupt/partial file — ignore; the owner rewrites it on heartbeat.
                }
            }
            return result;
        }

        /// <summary>The descriptor for a project, or null.</summary>
        public InstanceDescriptor FindByProjectPath(string projectPath)
        {
            var path = Path.Combine(_directory, FileNameFor(projectPath));
            if (!File.Exists(path)) return null;
            try
            {
                var descriptor = InstanceDescriptor.FromJson(File.ReadAllText(path));
                // Hash collision guard: the payload's path must actually match.
                if (descriptor != null &&
                    EndpointAddressing.Normalize(descriptor.ProjectPath) == EndpointAddressing.Normalize(projectPath))
                {
                    return descriptor;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Deletes stale descriptors; returns how many were reaped.</summary>
        public int ReapStale(DateTime nowUtc)
        {
            int reaped = 0;
            if (!System.IO.Directory.Exists(_directory)) return 0;
            foreach (var file in System.IO.Directory.GetFiles(_directory, "*.json"))
            {
                bool stale;
                try
                {
                    var descriptor = InstanceDescriptor.FromJson(File.ReadAllText(file));
                    stale = descriptor == null || !descriptor.IsFresh(nowUtc, StaleAfter);
                }
                catch
                {
                    stale = true;
                }
                if (stale)
                {
                    try { File.Delete(file); reaped++; } catch { /* held elsewhere — skip */ }
                }
            }
            return reaped;
        }
    }
}
