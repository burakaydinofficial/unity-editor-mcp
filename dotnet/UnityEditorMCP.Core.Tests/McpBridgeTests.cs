using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class McpBridgeTests
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

        private static async Task WaitUntil(Func<bool> condition)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed > Timeout) throw new TimeoutException("condition not met in time");
                await Task.Delay(5);
            }
        }

        private static async Task<NetworkStream> ConnectAsync(McpBridge bridge)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, bridge.Port).WaitAsync(Timeout);
            return client.GetStream();
        }

        private static async Task SendFrameAsync(NetworkStream stream, string payload)
        {
            var bytes = MessageFramer.Encode(payload);
            await stream.WriteAsync(bytes, 0, bytes.Length).WaitAsync(Timeout);
        }

        [Fact]
        public async Task EndToEnd_Command_RoundTripsThroughTheBridge()
        {
            using var bridge = new McpBridge(IPAddress.Loopback, 0);
            bridge.Register("ping", p => HandlerOutcome.Ok(new { pong = true, echo = (string)p["msg"] }));
            bridge.Start();

            var stream = await ConnectAsync(bridge);
            await SendFrameAsync(stream, "{\"id\":\"1\",\"type\":\"ping\",\"params\":{\"msg\":\"hi\"}}");

            await WaitUntil(() => bridge.PendingCount > 0); // command received off the wire
            Assert.Equal(1, bridge.Drain());                // simulate the editor main-thread pump

            var reply = JObject.Parse(await ReadOneFramedAsync(stream));
            Assert.Equal("1", (string)reply["id"]);
            Assert.Equal("success", (string)reply["status"]);
            Assert.True((bool)reply["result"]["pong"]);
            Assert.Equal("hi", (string)reply["result"]["echo"]);
        }

        [Fact]
        public async Task EndToEnd_UnknownCommand_ReturnsUnknownCommandError()
        {
            using var bridge = new McpBridge(IPAddress.Loopback, 0);
            bridge.Start();

            var stream = await ConnectAsync(bridge);
            await SendFrameAsync(stream, "{\"id\":\"7\",\"type\":\"does_not_exist\"}");

            await WaitUntil(() => bridge.PendingCount > 0);
            bridge.Drain();

            var reply = JObject.Parse(await ReadOneFramedAsync(stream));
            Assert.Equal("error", (string)reply["status"]);
            Assert.Equal("UNKNOWN_COMMAND", (string)reply["code"]);
        }

        [Fact]
        public async Task EndToEnd_MalformedJson_AnsweredImmediately_NoDrainNeeded()
        {
            using var bridge = new McpBridge(IPAddress.Loopback, 0);
            bridge.Start();

            var stream = await ConnectAsync(bridge);
            await SendFrameAsync(stream, "this is not json {");

            // Parse errors are answered on the transport thread — no Drain required.
            var reply = JObject.Parse(await ReadOneFramedAsync(stream));
            Assert.Equal("error", (string)reply["status"]);
            Assert.Equal("PARSE_ERROR", (string)reply["code"]);
        }
    }
}
