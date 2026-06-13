using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    /// <summary>
    /// Fast, Unity-independent coverage for the wire-truth classifier — the logic that
    /// used to live in the editor's Response.Result (untested there) and silently
    /// dropped inline-payload success envelopes to {} on the wire.
    /// </summary>
    public class ResponseClassifierTests
    {
        // --- Success: inline payloads must survive (the regression these guard) ---

        [Fact]
        public void InlineSuccessEnvelope_PreservesFields()
        {
            var o = ResponseClassifier.Classify(new JObject { ["success"] = true, ["isCompiling"] = false, ["count"] = 3 });
            Assert.False(o.IsError);
            var p = (JObject)o.Payload;
            Assert.False((bool)p["isCompiling"]);
            Assert.Equal(3, (int)p["count"]);
        }

        [Fact]
        public void StatusSuccessWithStatePayload_PreservesState()
        {
            var o = ResponseClassifier.Classify(new JObject { ["status"] = "success", ["state"] = new JObject { ["isPlaying"] = true } });
            Assert.False(o.IsError);
            Assert.True((bool)((JObject)o.Payload)["state"]["isPlaying"]);
        }

        // --- Success: explicit wrappers are unwrapped ---

        [Fact]
        public void ExplicitResultWrapper_IsUnwrapped()
        {
            var o = ResponseClassifier.Classify(new JObject { ["status"] = "success", ["result"] = new JObject { ["a"] = 1 } });
            Assert.False(o.IsError);
            Assert.Equal(1, (int)((JObject)o.Payload)["a"]);
        }

        [Fact]
        public void ExplicitDataWrapper_IsUnwrapped()
        {
            var o = ResponseClassifier.Classify(new JObject { ["success"] = true, ["data"] = new JObject { ["b"] = 2 } });
            Assert.False(o.IsError);
            Assert.Equal(2, (int)((JObject)o.Payload)["b"]);
        }

        // --- Errors: classified, not laundered ---

        [Fact]
        public void ErrorObject_IsError_WithDefaultCode()
        {
            var o = ResponseClassifier.Classify(new JObject { ["error"] = "boom" });
            Assert.True(o.IsError);
            Assert.Equal("boom", o.Error);
            Assert.Equal("EDITOR_ERROR", o.Code);
        }

        [Fact]
        public void ErrorObject_KeepsExplicitCode()
        {
            var o = ResponseClassifier.Classify(new JObject { ["error"] = "x", ["code"] = "VALIDATION_ERROR" });
            Assert.True(o.IsError);
            Assert.Equal("VALIDATION_ERROR", o.Code);
        }

        [Fact]
        public void SuccessTrue_OverridesErrorField()
        {
            // The bridge convention: success:true means "not an error" even if an
            // 'error' field is present (mirrors Node isHandlerLevelError).
            var o = ResponseClassifier.Classify(new JObject { ["success"] = true, ["error"] = "ignored" });
            Assert.False(o.IsError);
        }

        // --- Serialized envelope STRINGS (ScriptHandler/TestRunnerHandler) ---

        [Fact]
        public void SerializedErrorEnvelopeString_IsReparsedToError()
        {
            var o = ResponseClassifier.Classify("{\"status\":\"error\",\"error\":\"nope\",\"code\":\"Y\"}");
            Assert.True(o.IsError);
            Assert.Equal("nope", o.Error);
            Assert.Equal("Y", o.Code);
        }

        [Fact]
        public void SerializedSuccessEnvelopeString_IsReparsedAndUnwrapped()
        {
            var o = ResponseClassifier.Classify("{\"id\":\"1\",\"status\":\"success\",\"result\":{\"k\":1}}");
            Assert.False(o.IsError);
            Assert.Equal(1, (int)((JObject)o.Payload)["k"]);
        }

        // --- Opaque strings/data must NOT be reinterpreted ---

        [Fact]
        public void PlainString_StaysOpaqueSuccess()
        {
            var o = ResponseClassifier.Classify("hello");
            Assert.False(o.IsError);
            Assert.Equal("hello", o.Payload.ToString());
        }

        [Fact]
        public void ArrayString_KeepsItsWireType()
        {
            var o = ResponseClassifier.Classify("[1,2,3]");
            Assert.False(o.IsError);
            Assert.Equal(JTokenType.String, ((JToken)o.Payload).Type);
            Assert.Equal("[1,2,3]", o.Payload.ToString());
        }

        [Fact]
        public void NonEnvelopeObjectString_StaysOpaque()
        {
            // Parses as JSON but carries no status/success/error -> it is data, not an
            // envelope, so it stays a string rather than being restructured.
            var o = ResponseClassifier.Classify("{\"name\":\"x\"}");
            Assert.False(o.IsError);
            Assert.Equal(JTokenType.String, ((JToken)o.Payload).Type);
        }

        [Fact]
        public void NonJsonBraceString_StaysOpaque()
        {
            var o = ResponseClassifier.Classify("{ not json");
            Assert.False(o.IsError);
            Assert.Equal("{ not json", o.Payload.ToString());
        }

        // --- Non-object payloads ---

        [Fact]
        public void Array_IsSuccessPayload()
        {
            var o = ResponseClassifier.Classify(new[] { 1, 2, 3 });
            Assert.False(o.IsError);
            Assert.Equal(JTokenType.Array, ((JToken)o.Payload).Type);
        }

        [Fact]
        public void Null_IsEmptySuccess()
        {
            var o = ResponseClassifier.Classify(null);
            Assert.False(o.IsError);
            Assert.Null(o.Payload);
        }
    }
}
