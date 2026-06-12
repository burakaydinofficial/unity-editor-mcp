using System;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class DispatcherTests
    {
        private static CommandRequest Req(string id, string type, JObject p = null) =>
            new CommandRequest { Id = id, Type = type, Params = p };

        [Fact]
        public void SuccessOutcome_ProducesSuccessEnvelope()
        {
            var d = new CommandDispatcher();
            d.Register("ping", p => HandlerOutcome.Ok(new { pong = true }));
            var json = JObject.Parse(d.Dispatch(Req("1", "ping")).ToJson());
            Assert.Equal("1", (string)json["id"]);
            Assert.Equal("success", (string)json["status"]);
            Assert.True((bool)json["result"]["pong"]);
            Assert.Null(json["error"]);
        }

        [Fact]
        public void ErrorOutcome_IsNotLaunderedAsSuccess()
        {
            var d = new CommandDispatcher();
            d.Register("boom", p => HandlerOutcome.Fail("nope", "VALIDATION_ERROR"));
            var json = JObject.Parse(d.Dispatch(Req("2", "boom")).ToJson());
            Assert.Equal("error", (string)json["status"]);
            Assert.Equal("nope", (string)json["error"]);
            Assert.Equal("VALIDATION_ERROR", (string)json["code"]);
            Assert.Null(json["result"]);
        }

        [Fact]
        public void UnknownCommand_YieldsUnknownCommandError()
        {
            var d = new CommandDispatcher();
            var json = JObject.Parse(d.Dispatch(Req("3", "does_not_exist")).ToJson());
            Assert.Equal("error", (string)json["status"]);
            Assert.Equal("UNKNOWN_COMMAND", (string)json["code"]);
        }

        [Fact]
        public void ThrowingHandler_YieldsInternalError_NotCrash()
        {
            var d = new CommandDispatcher();
            d.Register("throws", p => throw new InvalidOperationException("kaboom"));
            var json = JObject.Parse(d.Dispatch(Req("4", "throws")).ToJson());
            Assert.Equal("error", (string)json["status"]);
            Assert.Equal("INTERNAL_ERROR", (string)json["code"]);
            Assert.Contains("kaboom", (string)json["error"]);
        }

        [Fact]
        public void Params_DefaultToEmptyObject_WhenNull()
        {
            var d = new CommandDispatcher();
            d.Register("needs_params", p => HandlerOutcome.Ok(new { count = p.Count }));
            var json = JObject.Parse(d.Dispatch(Req("5", "needs_params")).ToJson());
            Assert.Equal("success", (string)json["status"]);
            Assert.Equal(0, (int)json["result"]["count"]);
        }

        [Fact]
        public void DuplicateRegistration_Throws()
        {
            var d = new CommandDispatcher();
            d.Register("x", p => HandlerOutcome.Ok(null));
            Assert.Throws<InvalidOperationException>(() => d.Register("x", p => HandlerOutcome.Ok(null)));
        }

        [Fact]
        public void Registration_IsCaseInsensitive()
        {
            var d = new CommandDispatcher();
            d.Register("Ping", p => HandlerOutcome.Ok(true));
            Assert.True(d.IsRegistered("ping"));
        }
    }
}
