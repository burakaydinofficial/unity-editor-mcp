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
        public async Task ConcurrentClients_EachReceivesItsOwnResponse()
        {
            // ADR 0005 ("many MCP servers -> one editor"): the transport accepts concurrent clients,
            // and each queued command carries its originating client's responder, so replies route
            // back without cross-talk. This proves the Core composition the editor bootstrap uses.
            using var bridge = new McpBridge(IPAddress.Loopback, 0);
            bridge.Register("echo", p => HandlerOutcome.Ok(new { who = (string)p["who"] }));
            bridge.Start();

            using var clientA = new TcpClient();
            using var clientB = new TcpClient();
            await clientA.ConnectAsync(IPAddress.Loopback, bridge.Port).WaitAsync(Timeout);
            await clientB.ConnectAsync(IPAddress.Loopback, bridge.Port).WaitAsync(Timeout);
            var streamA = clientA.GetStream();
            var streamB = clientB.GetStream();

            await SendFrameAsync(streamA, "{\"id\":\"A\",\"type\":\"echo\",\"params\":{\"who\":\"alice\"}}");
            await SendFrameAsync(streamB, "{\"id\":\"B\",\"type\":\"echo\",\"params\":{\"who\":\"bob\"}}");

            await WaitUntil(() => bridge.PendingCount >= 2); // both received off the wire
            bridge.Drain();                                  // main-thread pump processes both

            var replyA = JObject.Parse(await ReadOneFramedAsync(streamA));
            var replyB = JObject.Parse(await ReadOneFramedAsync(streamB));

            // Each client gets ITS OWN response — no cross-routing.
            Assert.Equal("A", (string)replyA["id"]);
            Assert.Equal("alice", (string)replyA["result"]["who"]);
            Assert.Equal("B", (string)replyB["id"]);
            Assert.Equal("bob", (string)replyB["result"]["who"]);
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
