using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class FramingTests
    {
        [Fact]
        public void EncodeThenDecode_RoundTrips()
        {
            var f = new MessageFramer();
            f.Append(MessageFramer.Encode("{\"hello\":1}"));
            Assert.True(f.TryReadMessage(out var msg));
            Assert.Equal("{\"hello\":1}", msg);
            Assert.False(f.TryReadMessage(out _));
        }

        [Fact]
        public void PartialData_ReturnsFalse_ThenCompletes()
        {
            var framed = MessageFramer.Encode("abcdef");
            var f = new MessageFramer();
            f.Append(framed, 0, 5); // header + 1 body byte
            Assert.False(f.TryReadMessage(out _));
            f.Append(framed, 5, framed.Length - 5);
            Assert.True(f.TryReadMessage(out var msg));
            Assert.Equal("abcdef", msg);
        }

        [Fact]
        public void TwoConcatenatedMessages_BothRead()
        {
            var f = new MessageFramer();
            f.Append(MessageFramer.Encode("one"));
            f.Append(MessageFramer.Encode("two"));
            Assert.True(f.TryReadMessage(out var m1));
            Assert.Equal("one", m1);
            Assert.True(f.TryReadMessage(out var m2));
            Assert.Equal("two", m2);
            Assert.False(f.TryReadMessage(out _));
        }

        [Fact]
        public void OversizeLengthPrefix_Throws()
        {
            var f = new MessageFramer();
            f.Append(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 0x00 }); // ~2GB declared length
            Assert.Throws<FramingException>(() => f.TryReadMessage(out _));
        }

        [Fact]
        public void Utf8Multibyte_RoundTrips()
        {
            const string s = "ünïcödé-✓-日本語";
            var f = new MessageFramer();
            f.Append(MessageFramer.Encode(s));
            Assert.True(f.TryReadMessage(out var msg));
            Assert.Equal(s, msg);
        }
    }
}
