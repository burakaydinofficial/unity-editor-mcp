using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class AuditLogTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "uemcp-audit-" + Guid.NewGuid().ToString("N") + ".jsonl");
        public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

        [Fact]
        public void Append_Then_Read_RoundTrips()
        {
            AuditLog.Append(_path, "delete_asset", "Assets/Foo.asset", false);
            var entries = AuditLog.Read(_path, 100, null, null);
            Assert.Single(entries);
            Assert.Equal("delete_asset", (string)entries[0]["type"]);
            Assert.Equal("Assets/Foo.asset", (string)entries[0]["target"]);
            Assert.False((bool)entries[0]["ok"]);
            Assert.NotNull(entries[0]["t"]);
        }

        [Fact]
        public void Read_FiltersByType()
        {
            AuditLog.Append(_path, "delete_asset", "a", true);
            AuditLog.Append(_path, "create_material", "b", true);
            AuditLog.Append(_path, "delete_gameobject", "c", true);
            Assert.Equal(2, AuditLog.Read(_path, 100, "delete", null).Count);
        }

        [Fact]
        public void Read_FiltersBySince()
        {
            AuditLog.Append(_path, "old", "x", true);
            System.Threading.Thread.Sleep(30); // > Windows UtcNow ~15ms tick, so timestamps are distinct
            var cutoff = DateTime.UtcNow.ToString("o");
            System.Threading.Thread.Sleep(30);
            AuditLog.Append(_path, "new", "y", true);
            var recent = AuditLog.Read(_path, 100, null, cutoff);
            Assert.Single(recent);
            Assert.Equal("new", (string)recent[0]["type"]);
        }

        [Fact]
        public void Read_Since_ShortForm_IncludesSameSecondEntries()
        {
            AuditLog.Append(_path, "cmd", "x", true);
            var stamp = (string)AuditLog.Read(_path, 100, null, null)[0]["t"]; // ...SS.fffffffZ
            var shortForm = stamp.Substring(0, 19) + "Z";                       // ...SSZ (no fractional, Z)
            Assert.Single(AuditLog.Read(_path, 100, null, shortForm));          // normalized -> same-second entry kept
        }

        [Fact]
        public void Read_CapsByMax()
        {
            for (int i = 0; i < 10; i++) AuditLog.Append(_path, "set_x", i.ToString(), true);
            var last3 = AuditLog.Read(_path, 3, null, null);
            Assert.Equal(3, last3.Count);
            Assert.Equal("9", (string)last3[2]["target"]); // chronological — last is newest
        }

        [Fact]
        public void Read_SkipsMalformedLines()
        {
            File.WriteAllText(_path, "not json\n" +
                new JObject { ["t"] = "2026", ["type"] = "ok_cmd", ["target"] = "x", ["ok"] = true }.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            var entries = AuditLog.Read(_path, 100, null, null);
            Assert.Single(entries);
            Assert.Equal("ok_cmd", (string)entries[0]["type"]);
        }

        [Fact]
        public void Append_OverCap_DropsOldestHalf()
        {
            for (int i = 0; i < 50; i++) AuditLog.Append(_path, "cmd", new string('x', 200), true);
            var before = AuditLog.Read(_path, 1000, null, null).Count;
            AuditLog.Append(_path, "cmd_after", "tail", true, capBytes: 1000); // tiny cap forces a truncate
            var entries = AuditLog.Read(_path, 1000, null, null);
            Assert.True(entries.Count < before, "oldest half dropped");
            Assert.Equal("cmd_after", (string)entries[entries.Count - 1]["type"]); // newest survived
        }

        [Fact]
        public void Clear_EmptiesIt()
        {
            AuditLog.Append(_path, "cmd", "x", true);
            AuditLog.Clear(_path);
            Assert.Empty(AuditLog.Read(_path, 100, null, null));
        }

        [Fact]
        public void Append_BadInput_DoesNotThrow()
        {
            AuditLog.Append("", "cmd", "x", true);
            AuditLog.Append(null, "cmd", "x", true);
            AuditLog.Append(_path, null, "x", true); // null type ignored
            Assert.Empty(AuditLog.Read(_path, 100, null, null));
        }
    }
}
