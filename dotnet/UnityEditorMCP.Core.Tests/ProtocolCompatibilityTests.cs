using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class ProtocolCompatibilityTests
    {
        [Theory]
        [InlineData("1.0.0", "1.0.0")]
        [InlineData("1.4.0", "1.2.7")] // minor/patch skew within a major
        [InlineData("2.10.3", "2.0.0")]
        public void SameMajor_IsCompatible(string local, string remote)
        {
            var r = ProtocolCompatibility.Check(local, remote);
            Assert.True(r.Compatible);
            Assert.Null(r.Code);
        }

        [Theory]
        [InlineData("2.0.0", "1.9.9")]
        [InlineData("1.0.0", "3.0.0")]
        public void DifferentMajor_IsIncompatible(string local, string remote)
        {
            var r = ProtocolCompatibility.Check(local, remote);
            Assert.False(r.Compatible);
            Assert.Equal("PROTOCOL_VERSION_MISMATCH", r.Code);
        }

        [Theory]
        [InlineData("bad", "1.0.0")]
        [InlineData("1.0", "1.0.0")]
        [InlineData("", "1.0.0")]
        [InlineData(null, "1.0.0")]
        public void Unparseable_IsIncompatible(string local, string remote)
        {
            var r = ProtocolCompatibility.Check(local, remote);
            Assert.False(r.Compatible);
            Assert.Equal("PROTOCOL_VERSION_MISMATCH", r.Code);
        }

        [Fact]
        public void TryParse_ValidAndInvalid()
        {
            Assert.True(ProtocolCompatibility.TryParse("1.2.3", out var maj, out var min, out var pat));
            Assert.Equal((1, 2, 3), (maj, min, pat));
            Assert.False(ProtocolCompatibility.TryParse("1.2", out _, out _, out _));
            Assert.False(ProtocolCompatibility.TryParse("x.y.z", out _, out _, out _));
        }
    }
}
