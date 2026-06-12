using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class TcpTransportTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private static async Task<string> ReadOneFramedAsync(NetworkStream stream)
        {
            var framer = new MessageFramer();
            var buf = new byte[4096];
            while (true)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length).WaitAsync(Timeout);
                if (n == 0) throw new IOException("connection closed before a full frame");
                framer.Append(buf, 0, n);
                if (framer.TryReadMessage(out var msg)) return msg;
            }
        }

        [Fact]
        public async Task RoundTrip_FramedRequest_GetsFramedReply()
        {
            using var transport = new TcpTransport(IPAddress.Loopback, 0); // ephemeral port
            transport.MessageReceived += (msg, respond) => respond("echo:" + msg);
            transport.Start();
            Assert.True(transport.Port > 0);
            Assert.True(transport.IsListening);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, transport.Port).WaitAsync(Timeout);
            var stream = client.GetStream();

            var request = MessageFramer.Encode("hello");
            await stream.WriteAsync(request, 0, request.Length).WaitAsync(Timeout);

            Assert.Equal("echo:hello", await ReadOneFramedAsync(stream));
        }

        [Fact]
        public async Task MultipleFramedMessages_AreEachDelivered()
        {
            using var transport = new TcpTransport(IPAddress.Loopback, 0);
            transport.MessageReceived += (msg, respond) => respond(msg.ToUpperInvariant());
            transport.Start();

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, transport.Port).WaitAsync(Timeout);
            var stream = client.GetStream();

            // Two frames in a single write — the framer must split them.
            var a = MessageFramer.Encode("one");
            var b = MessageFramer.Encode("two");
            var both = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, both, 0, a.Length);
            Buffer.BlockCopy(b, 0, both, a.Length, b.Length);
            await stream.WriteAsync(both, 0, both.Length).WaitAsync(Timeout);

            Assert.Equal("ONE", await ReadOneFramedAsync(stream));
            Assert.Equal("TWO", await ReadOneFramedAsync(stream));
        }

        [Fact]
        public void Stop_StopsListening()
        {
            var transport = new TcpTransport(IPAddress.Loopback, 0);
            transport.Start();
            Assert.True(transport.IsListening);
            transport.Stop();
            Assert.False(transport.IsListening);
        }
    }
}
