using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class HandshakeTests
    {
        [Fact]
        public void DefaultProtocolVersion_MatchesGeneratedCatalog()
        {
            Assert.Equal(CommandCatalog.ProtocolVersion, new Handshake().ProtocolVersion);
        }

        [Fact]
        public void ToJson_FromJson_RoundTrips()
        {
            var h = new Handshake
            {
                ProtocolVersion = "1.0.0",
                UnityVersion = "2020.3.40f1",
                ProjectPath = "C:/proj",
                AvailableCommands = new[] { "ping", "add_component" },
            };
            var back = Handshake.FromJson(h.ToJson());
            Assert.Equal("1.0.0", back.ProtocolVersion);
            Assert.Equal("2020.3.40f1", back.UnityVersion);
            Assert.Equal("C:/proj", back.ProjectPath);
            Assert.Equal(new[] { "ping", "add_component" }, back.AvailableCommands);
        }

        [Fact]
        public void FromJson_MissingAvailableCommands_IsEmpty()
        {
            var back = Handshake.FromJson("{\"protocolVersion\":\"1.0.0\"}");
            Assert.Empty(back.AvailableCommands);
        }

        [Fact]
        public void CheckAgainst_SameMajorAndMatchingPath_IsCompatible()
        {
            var h = new Handshake { ProtocolVersion = "1.2.0", ProjectPath = "C:/proj" };
            var r = h.CheckAgainst("1.0.0", "C:/proj");
            Assert.True(r.Compatible);
        }

        [Fact]
        public void CheckAgainst_MajorMismatch_IsProtocolVersionMismatch()
        {
            var h = new Handshake { ProtocolVersion = "2.0.0" };
            var r = h.CheckAgainst("1.0.0");
            Assert.False(r.Compatible);
            Assert.Equal("PROTOCOL_VERSION_MISMATCH", r.Code);
        }

        [Fact]
        public void CheckAgainst_ProjectPathMismatch_IsReported()
        {
            var h = new Handshake { ProtocolVersion = "1.0.0", ProjectPath = "C:/other" };
            var r = h.CheckAgainst("1.0.0", "C:/proj");
            Assert.False(r.Compatible);
            Assert.Equal("PROJECT_PATH_MISMATCH", r.Code);
        }

        [Theory]
        [InlineData("C:/proj", "C:\\proj\\")]
        [InlineData("C:/proj/", "C:/PROJ")]
        public void CheckAgainst_NormalizesPathSeparatorsCaseAndTrailingSlash(string expected, string actual)
        {
            var h = new Handshake { ProtocolVersion = "1.0.0", ProjectPath = actual };
            Assert.True(h.CheckAgainst("1.0.0", expected).Compatible);
        }
    }
}
