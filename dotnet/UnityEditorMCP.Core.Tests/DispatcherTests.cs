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

        // --- H3 confirm-gate ---

        [Fact]
        public void RequiresConfirm_WithoutConfirm_RefusesAndDoesNotCallHandler()
        {
            var d = new CommandDispatcher();
            bool called = false;
            d.Register("nuke", p => { called = true; return HandlerOutcome.Ok(new { done = true }); }, requiresConfirm: true);
            var json = JObject.Parse(d.Dispatch(Req("5", "nuke")).ToJson());
            Assert.Equal("error", (string)json["status"]);
            Assert.Equal("CONFIRMATION_REQUIRED", (string)json["code"]);
            Assert.False(called); // gated before the handler runs
        }

        [Fact]
        public void RequiresConfirm_WithConfirmTrue_Dispatches()
        {
            var d = new CommandDispatcher();
            d.Register("nuke", p => HandlerOutcome.Ok(new { done = true }), requiresConfirm: true);
            var json = JObject.Parse(d.Dispatch(Req("6", "nuke", new JObject { ["confirm"] = true })).ToJson());
            Assert.Equal("success", (string)json["status"]);
            Assert.True((bool)json["result"]["done"]);
        }

        [Fact]
        public void NonConfirmCommand_IsUnaffectedByTheGate()
        {
            var d = new CommandDispatcher();
            d.Register("safe", p => HandlerOutcome.Ok(new { ok = true }));
            var json = JObject.Parse(d.Dispatch(Req("7", "safe")).ToJson());
            Assert.Equal("success", (string)json["status"]); // no confirm needed
            Assert.False(d.RequiresConfirm("safe"));
        }

        [Fact]
        public void Confirm_StaysInParams_ForHandlersThatReadIt()
        {
            var d = new CommandDispatcher();
            bool sawConfirm = false;
            d.Register("nuke", p => { sawConfirm = p["confirm"] != null && (bool)p["confirm"]; return HandlerOutcome.Ok(new { }); }, requiresConfirm: true);
            d.Dispatch(Req("8", "nuke", new JObject { ["confirm"] = true }));
            Assert.True(sawConfirm); // confirm is NOT stripped (unlike fields), so self-gating handlers still see it
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

        [Fact]
        public void Fallback_HandlesUnregisteredCommands()
        {
            var d = new CommandDispatcher();
            d.SetFallback(req => HandlerOutcome.Ok(new { handledBy = "legacy", type = req.Type }));
            var json = JObject.Parse(d.Dispatch(Req("1", "anything")).ToJson());
            Assert.Equal("success", (string)json["status"]);
            Assert.Equal("legacy", (string)json["result"]["handledBy"]);
            Assert.Equal("anything", (string)json["result"]["type"]);
        }

        [Fact]
        public void RegisteredHandler_TakesPrecedenceOverFallback()
        {
            var d = new CommandDispatcher();
            d.Register("ping", p => HandlerOutcome.Ok(new { from = "handler" }));
            d.SetFallback(req => HandlerOutcome.Ok(new { from = "fallback" }));
            var json = JObject.Parse(d.Dispatch(Req("1", "ping")).ToJson());
            Assert.Equal("handler", (string)json["result"]["from"]);
        }

        [Fact]
        public void Fallback_ErrorOutcome_IsNotLaundered()
        {
            var d = new CommandDispatcher();
            d.SetFallback(req => HandlerOutcome.Fail("legacy failure", "VALIDATION_ERROR"));
            var json = JObject.Parse(d.Dispatch(Req("1", "x")).ToJson());
            Assert.Equal("error", (string)json["status"]);
            Assert.Equal("VALIDATION_ERROR", (string)json["code"]);
        }

        [Fact]
        public void Fallback_Throwing_YieldsInternalError()
        {
            var d = new CommandDispatcher();
            d.SetFallback(req => throw new InvalidOperationException("boom"));
            var json = JObject.Parse(d.Dispatch(Req("1", "x")).ToJson());
            Assert.Equal("INTERNAL_ERROR", (string)json["code"]);
        }

        [Fact]
        public void NoFallback_StillYieldsUnknownCommand()
        {
            var d = new CommandDispatcher();
            var json = JObject.Parse(d.Dispatch(Req("1", "nope")).ToJson());
            Assert.Equal("UNKNOWN_COMMAND", (string)json["code"]);
        }
    }
}
