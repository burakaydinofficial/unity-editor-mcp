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
