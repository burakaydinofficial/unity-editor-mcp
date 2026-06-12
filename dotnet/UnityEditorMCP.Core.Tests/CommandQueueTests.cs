using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class CommandQueueTests
    {
        private static CommandDispatcher EchoDispatcher()
        {
            var d = new CommandDispatcher();
            d.Register("echo", p => HandlerOutcome.Ok(new { type = "echo" }));
            d.Register("a", p => HandlerOutcome.Ok(new { n = 1 }));
            d.Register("b", p => HandlerOutcome.Ok(new { n = 2 }));
            return d;
        }

        private static CommandRequest Req(string id, string type) =>
            new CommandRequest { Id = id, Type = type };

        [Fact]
        public void DrainAll_DispatchesFifo_AndDeliversResponses()
        {
            var q = new CommandQueue(EchoDispatcher());
            var ids = new List<string>();
            q.Enqueue(Req("1", "a"), r => ids.Add(r.Id));
            q.Enqueue(Req("2", "b"), r => ids.Add(r.Id));

            Assert.Equal(2, q.Count);
            Assert.Equal(2, q.DrainAll());
            Assert.Equal(new[] { "1", "2" }, ids.ToArray());
            Assert.Equal(0, q.Count);
            Assert.Equal(0, q.DrainAll());
        }

        [Fact]
        public void DrainAll_DeliversTheDispatchedResult()
        {
            var q = new CommandQueue(EchoDispatcher());
            string status = null;
            q.Enqueue(Req("9", "unknown_cmd"), r => status = (string)JObject.Parse(r.ToJson())["status"]);
            q.DrainAll();
            Assert.Equal("error", status); // unknown command -> proper error, still delivered
        }

        [Fact]
        public void ResponderThrowing_DoesNotStopTheDrain()
        {
            var q = new CommandQueue(EchoDispatcher());
            var delivered = new List<string>();
            q.Enqueue(Req("1", "a"), r => throw new InvalidOperationException("boom"));
            q.Enqueue(Req("2", "b"), r => delivered.Add(r.Id));
            Assert.Equal(2, q.DrainAll());
            Assert.Equal(new[] { "2" }, delivered.ToArray());
        }

        [Fact]
        public void Clear_DiscardsWithoutDispatching()
        {
            var q = new CommandQueue(EchoDispatcher());
            q.Enqueue(Req("1", "a"), r => throw new Exception("should not run"));
            q.Clear();
            Assert.Equal(0, q.Count);
            Assert.Equal(0, q.DrainAll());
        }

        [Fact]
        public void Enqueue_IsThreadSafe()
        {
            var q = new CommandQueue(EchoDispatcher());
            Parallel.For(0, 1000, i => q.Enqueue(Req(i.ToString(), "echo"), _ => { }));
            Assert.Equal(1000, q.Count);
            Assert.Equal(1000, q.DrainAll());
        }

        [Fact]
        public void NullArguments_Throw()
        {
            var q = new CommandQueue(EchoDispatcher());
            Assert.Throws<ArgumentNullException>(() => q.Enqueue(null, _ => { }));
            Assert.Throws<ArgumentNullException>(() => q.Enqueue(Req("1", "a"), null));
            Assert.Throws<ArgumentNullException>(() => new CommandQueue(null));
        }
    }
}
