using System.Linq;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class CatalogConformanceTests
    {
        [Fact]
        public void AllExpectedRegistered_IsOk()
        {
            var report = CatalogConformance.Check(
                new[] { "a", "b", "c" },
                new[] { "a", "b", "c" });
            Assert.True(report.Ok);
            Assert.Empty(report.MissingHandlers);
            Assert.Empty(report.UnexpectedHandlers);
        }

        [Fact]
        public void MissingHandler_IsReported()
        {
            var report = CatalogConformance.Check(new[] { "a", "b" }, new[] { "a" });
            Assert.False(report.Ok);
            Assert.Equal(new[] { "b" }, report.MissingHandlers.ToArray());
        }

        [Fact]
        public void UnexpectedHandler_IsReported()
        {
            var report = CatalogConformance.Check(new[] { "a" }, new[] { "a", "rogue" });
            Assert.False(report.Ok);
            Assert.Equal(new[] { "rogue" }, report.UnexpectedHandlers.ToArray());
        }

        [Fact]
        public void KnownGap_IsExcusedFromMissing()
        {
            var report = CatalogConformance.Check(
                new[] { "a", "get_component_types" },
                new[] { "a" },
                knownGaps: new[] { "get_component_types" });
            Assert.True(report.Ok);
        }

        [Fact]
        public void Check_IsCaseInsensitive()
        {
            var report = CatalogConformance.Check(new[] { "Ping" }, new[] { "ping" });
            Assert.True(report.Ok);
        }

        [Fact]
        public void DispatcherOverload_UsesRegisteredTypes()
        {
            var d = new CommandDispatcher();
            d.Register("a", p => HandlerOutcome.Ok(null));
            var report = CatalogConformance.Check(new[] { "a", "b" }, d);
            Assert.Equal(new[] { "b" }, report.MissingHandlers.ToArray());
        }

        [Fact]
        public void GeneratedCatalog_IsPopulated()
        {
            // Sanity: the committed CommandCatalog.g.cs was generated and compiled.
            Assert.True(ProtocolCompatibility.TryParse(CommandCatalog.ProtocolVersion, out _, out _, out _));
            Assert.Contains("create_gameobject", CommandCatalog.EditorCommands);
            Assert.True(CommandCatalog.EditorCommands.Length >= 60);
        }
    }
}
