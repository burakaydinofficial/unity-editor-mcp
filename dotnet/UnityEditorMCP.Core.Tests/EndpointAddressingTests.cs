using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class EndpointAddressingTests
    {
        [Theory]
        // Canonical FNV-1a/32 vectors (ASCII == UTF-16 code units), locked so the
        // Node server's implementation must match byte-for-byte.
        [InlineData("", 2166136261u)]
        [InlineData("a", 0xe40c292cu)]
        [InlineData("foobar", 0xbf9cf968u)]
        public void Fnv1a_MatchesCanonicalVectors(string input, uint expected)
        {
            Assert.Equal(expected, EndpointAddressing.Fnv1a(input));
        }

        [Fact]
        public void DerivePort_IsDeterministic()
        {
            Assert.Equal(
                EndpointAddressing.DerivePort("C:/projects/game"),
                EndpointAddressing.DerivePort("C:/projects/game"));
        }

        [Fact]
        public void DerivePort_DiffersByProject()
        {
            Assert.NotEqual(
                EndpointAddressing.DerivePort("C:/projects/2020"),
                EndpointAddressing.DerivePort("C:/projects/2021"));
        }

        [Theory]
        [InlineData("C:/projects/game", "C:\\projects\\game\\")]
        [InlineData("C:/projects/game", "C:/Projects/Game")]
        public void DerivePort_NormalizesSeparatorsCaseAndTrailingSlash(string a, string b)
        {
            Assert.Equal(EndpointAddressing.DerivePort(a), EndpointAddressing.DerivePort(b));
        }

        [Fact]
        public void Normalize_FoldsOnlyAsciiAZ_LeavingUnicodeIntact()
        {
            // ASCII letters fold; the Turkish dotted-I (U+0130) is left untouched so
            // it matches JS exactly (toLowerCase would split it into two code points).
            Assert.Equal("c:/proİject", EndpointAddressing.Normalize("C:/PROİJECT"));
            Assert.Equal("c:/proİject", EndpointAddressing.Normalize("C:\\PROİJECT\\"));
        }

        [Fact]
        public void DerivePort_StaysInRange()
        {
            for (var i = 0; i < 500; i++)
            {
                int port = EndpointAddressing.DerivePort($"C:/projects/p{i}");
                Assert.InRange(port, EndpointAddressing.DefaultBasePort,
                    EndpointAddressing.DefaultBasePort + EndpointAddressing.DefaultRange - 1);
            }
        }

        [Fact]
        public void DerivePort_RespectsCustomBaseAndRange()
        {
            int port = EndpointAddressing.DerivePort("anything", basePort: 9000, range: 10);
            Assert.InRange(port, 9000, 9009);
        }
    }
}
