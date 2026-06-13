using System;
using System.IO;
using System.Linq;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class InstanceRegistryTests : IDisposable
    {
        private readonly string _dir;
        private readonly InstanceRegistry _registry;

        public InstanceRegistryTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mcp-registry-tests-" + Guid.NewGuid().ToString("N"));
            _registry = new InstanceRegistry(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static InstanceDescriptor Descriptor(string projectPath, int port, DateTime? heartbeat = null)
        {
            var now = heartbeat ?? DateTime.UtcNow;
            return new InstanceDescriptor
            {
                ProjectPath = projectPath,
                Port = port,
                Pid = 1234,
                UnityVersion = "2020.3.49f1",
                StartedAtUtc = now,
                LastHeartbeatUtc = now,
            };
        }

        [Fact]
        public void ToJson_SerializesUtcDatesWithZSuffix_ParseableAsUtc()
        {
            var d = Descriptor("p", 1, new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc));
            var json = d.ToJson();
            Assert.Contains("\"lastHeartbeat\":", json.Replace(" ", ""));
            Assert.Matches("\"lastHeartbeat\": \"2026-06-13T10:00:00Z\"", json);
            // Round-trips back to the same UTC instant.
            Assert.Equal(d.LastHeartbeatUtc.ToUniversalTime(), InstanceDescriptor.FromJson(json).LastHeartbeatUtc.ToUniversalTime());
        }

        [Fact]
        public void Publish_ThenFindByProjectPath_RoundTrips()
        {
            _registry.Publish(Descriptor("C:/projects/game", 6512));
            var found = _registry.FindByProjectPath("C:/projects/game");
            Assert.NotNull(found);
            Assert.Equal(6512, found.Port);
            Assert.Equal(CommandCatalog.ProtocolVersion, found.ProtocolVersion);
        }

        [Fact]
        public void FindByProjectPath_NormalizesPath()
        {
            _registry.Publish(Descriptor("C:/projects/game", 6512));
            Assert.NotNull(_registry.FindByProjectPath("C:\\Projects\\Game\\"));
        }

        [Fact]
        public void Publish_Twice_Overwrites()
        {
            _registry.Publish(Descriptor("C:/projects/game", 6512));
            _registry.Publish(Descriptor("C:/projects/game", 7001));
            Assert.Equal(7001, _registry.FindByProjectPath("C:/projects/game").Port);
            Assert.Single(_registry.ReadAll());
        }

        [Fact]
        public void ReadAll_ListsAllInstances_AndSkipsCorrupt()
        {
            _registry.Publish(Descriptor("C:/projects/a", 6500));
            _registry.Publish(Descriptor("C:/projects/b", 6501));
            File.WriteAllText(Path.Combine(_dir, "corrupt.json"), "{ not json ");
            var all = _registry.ReadAll();
            Assert.Equal(2, all.Count);
            Assert.Equal(new[] { 6500, 6501 }, all.Select(d => d.Port).OrderBy(p => p).ToArray());
        }

        [Fact]
        public void Remove_DeletesDescriptor()
        {
            _registry.Publish(Descriptor("C:/projects/game", 6512));
            _registry.Remove("C:/projects/game");
            Assert.Null(_registry.FindByProjectPath("C:/projects/game"));
            Assert.Empty(_registry.ReadAll());
        }

        [Fact]
        public void IsFresh_RespectsStaleWindow()
        {
            var now = DateTime.UtcNow;
            Assert.True(Descriptor("p", 1, now - TimeSpan.FromSeconds(60)).IsFresh(now, InstanceRegistry.StaleAfter));
            Assert.False(Descriptor("p", 1, now - TimeSpan.FromSeconds(301)).IsFresh(now, InstanceRegistry.StaleAfter));
        }

        [Fact]
        public void ReapStale_DeletesOnlyStaleAndCorrupt()
        {
            var now = DateTime.UtcNow;
            _registry.Publish(Descriptor("C:/projects/fresh", 6500, now));
            _registry.Publish(Descriptor("C:/projects/stale", 6501, now - TimeSpan.FromSeconds(400)));
            File.WriteAllText(Path.Combine(_dir, "corrupt.json"), "{ not json ");

            Assert.Equal(2, _registry.ReapStale(now));
            var all = _registry.ReadAll();
            Assert.Single(all);
            Assert.Equal("C:/projects/fresh", all[0].ProjectPath);
        }

        [Fact]
        public void ReadAll_OnMissingDirectory_IsEmpty()
        {
            var registry = new InstanceRegistry(Path.Combine(_dir, "does-not-exist"));
            Assert.Empty(registry.ReadAll());
            Assert.Equal(0, registry.ReapStale(DateTime.UtcNow));
        }

        [Fact]
        public void IsLive_SameHost_TrustsProcessLiveness_OverHeartbeat()
        {
            var now = DateTime.UtcNow;
            var d = Descriptor("p", 1, now - TimeSpan.FromSeconds(9999)); // ancient heartbeat
            d.Host = "HOST-A";
            // Same host + pid alive -> live despite the stale heartbeat.
            Assert.True(InstanceRegistry.IsLive(d, now, "host-a", pid => true));
            // Same host + pid dead -> not live even if heartbeat were fresh.
            Assert.False(InstanceRegistry.IsLive(d, now, "HOST-A", pid => false));
        }

        [Fact]
        public void IsLive_OtherOrUnknownHost_FallsBackToHeartbeat()
        {
            var now = DateTime.UtcNow;
            var fresh = Descriptor("p", 1, now);
            fresh.Host = "OTHER";
            var stale = Descriptor("p", 1, now - TimeSpan.FromSeconds(400));
            stale.Host = "OTHER";
            // Different host: pid check is meaningless, use the heartbeat.
            Assert.True(InstanceRegistry.IsLive(fresh, now, "HOST-A", pid => false));
            Assert.False(InstanceRegistry.IsLive(stale, now, "HOST-A", pid => true));
            // Null host: also heartbeat-based.
            Assert.True(InstanceRegistry.IsLive(Descriptor("p", 1, now), now, "HOST-A", pid => false));
        }

        [Fact]
        public void ReapStale_SameHost_ReapsCrashedPid_EvenWithFreshHeartbeat()
        {
            var now = DateTime.UtcNow;
            var alive = Descriptor("C:/projects/alive", 6500, now);
            alive.Host = "HOST-A"; alive.Pid = 1111;
            var crashed = Descriptor("C:/projects/crashed", 6501, now); // fresh heartbeat...
            crashed.Host = "HOST-A"; crashed.Pid = 2222;                 // ...but pid is gone
            _registry.Publish(alive);
            _registry.Publish(crashed);

            int reaped = _registry.ReapStale(now, "HOST-A", pid => pid == 1111);
            Assert.Equal(1, reaped);
            var remaining = _registry.ReadAll();
            Assert.Single(remaining);
            Assert.Equal("C:/projects/alive", remaining[0].ProjectPath);
        }

        [Fact]
        public void ReapStale_RemovesOrphanTmpFiles()
        {
            var old = Path.Combine(_dir, "abc.1234.tmp");
            Directory.CreateDirectory(_dir);
            File.WriteAllText(old, "partial");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromSeconds(400));
            _registry.ReapStale(DateTime.UtcNow);
            Assert.False(File.Exists(old));
        }

        [Fact]
        public void Publish_AtomicReplace_OverwritesExisting()
        {
            _registry.Publish(Descriptor("C:/projects/game", 6500));
            _registry.Publish(Descriptor("C:/projects/game", 7000));
            Assert.Equal(7000, _registry.FindByProjectPath("C:/projects/game").Port);
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp")); // tmp cleaned up
        }

        [Fact]
        public void FileNameFor_IsStableAndNormalized()
        {
            Assert.Equal(InstanceRegistry.FileNameFor("C:/projects/game"), InstanceRegistry.FileNameFor("C:\\Projects\\Game\\"));
            Assert.EndsWith(".json", InstanceRegistry.FileNameFor("x"));
        }

        [Fact]
        public void DefaultDirectory_RespectsEnvOverride()
        {
            var original = Environment.GetEnvironmentVariable(InstanceRegistry.DirectoryEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(InstanceRegistry.DirectoryEnvVar, "X:/custom/registry");
                Assert.Equal("X:/custom/registry", InstanceRegistry.DefaultDirectory());
            }
            finally
            {
                Environment.SetEnvironmentVariable(InstanceRegistry.DirectoryEnvVar, original);
            }
        }

        [Fact]
        public void DefaultDirectory_EndsWithSharedSubpath()
        {
            var original = Environment.GetEnvironmentVariable(InstanceRegistry.DirectoryEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(InstanceRegistry.DirectoryEnvVar, null);
                var dir = InstanceRegistry.DefaultDirectory().Replace('\\', '/');
                Assert.EndsWith("unity-editor-mcp/instances", dir);
            }
            finally
            {
                Environment.SetEnvironmentVariable(InstanceRegistry.DirectoryEnvVar, original);
            }
        }
    }
}
