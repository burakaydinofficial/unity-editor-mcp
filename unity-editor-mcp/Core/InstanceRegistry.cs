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
            // Unique tmp per writer so a crashed publish never collides with another.
            var tmp = path + "." + descriptor.Pid + ".tmp";
            File.WriteAllText(tmp, descriptor.ToJson());
            try
            {
                // File.Replace is atomic on NTFS (no window with a missing file);
                // it requires the destination to exist, so Move when it doesn't.
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (IOException)
            {
                // Cross-volume or transient failure — best-effort replace.
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* ignore */ } }
            }
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

        /// <summary>
        /// Whether a descriptor represents a live editor. On the SAME host, process
        /// liveness is authoritative (a crashed editor's pid is gone immediately,
        /// collapsing the heartbeat-timeout window to ~0, and an idle-but-alive
        /// editor stays discoverable even if its update pump stalled). For an unknown
        /// or different host we can't check the pid, so fall back to the heartbeat.
        /// </summary>
        public static bool IsLive(InstanceDescriptor descriptor, DateTime nowUtc, string currentHost, Func<int, bool> isProcessAlive = null)
        {
            if (descriptor == null) return false;
            isProcessAlive = isProcessAlive ?? IsProcessAlive;
            if (!string.IsNullOrEmpty(descriptor.Host) && !string.IsNullOrEmpty(currentHost) &&
                string.Equals(descriptor.Host, currentHost, StringComparison.OrdinalIgnoreCase))
            {
                return isProcessAlive(descriptor.Pid);
            }
            return descriptor.IsFresh(nowUtc, StaleAfter);
        }

        /// <summary>True if a process with this id currently exists on this host.</summary>
        public static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (System.Diagnostics.Process.GetProcessById(pid)) { return true; }
            }
            catch (ArgumentException)
            {
                return false; // no such process
            }
            catch
            {
                return true; // can't tell (e.g. access denied) — assume alive, don't reap
            }
        }

        /// <summary>
        /// Deletes descriptors that are no longer live (crashed pid on this host, or a
        /// stale heartbeat for unknown/other hosts) plus orphaned *.tmp files; returns
        /// how many descriptors were reaped.
        /// </summary>
        public int ReapStale(DateTime nowUtc, string currentHost = null, Func<int, bool> isProcessAlive = null)
        {
            int reaped = 0;
            if (!System.IO.Directory.Exists(_directory)) return 0;

            foreach (var file in System.IO.Directory.GetFiles(_directory, "*.json"))
            {
                bool dead;
                try
                {
                    var descriptor = InstanceDescriptor.FromJson(File.ReadAllText(file));
                    dead = !IsLive(descriptor, nowUtc, currentHost, isProcessAlive);
                }
                catch
                {
                    dead = true; // corrupt
                }
                if (dead)
                {
                    try { File.Delete(file); reaped++; } catch { /* held elsewhere — skip */ }
                }
            }

            // Orphan tmp files from a crashed publish (an in-flight tmp is seconds old).
            foreach (var tmp in System.IO.Directory.GetFiles(_directory, "*.tmp"))
            {
                try
                {
                    if (nowUtc - File.GetLastWriteTimeUtc(tmp) > StaleAfter) File.Delete(tmp);
                }
                catch { /* ignore */ }
            }

            return reaped;
        }
    }
}
