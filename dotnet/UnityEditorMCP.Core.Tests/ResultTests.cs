using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class ResultTests
    {
        [Fact]
        public void NullPayload_SerializesAsJsonNull()
        {
            var json = JObject.Parse(CommandResult.FromOutcome("1", HandlerOutcome.Ok(null)).ToJson());
            Assert.Equal("success", (string)json["status"]);
            Assert.Equal(JTokenType.Null, json["result"].Type);
        }

        [Fact]
        public void ErrorWithRemediationAndDetails_AreIncluded()
        {
            var outcome = HandlerOutcome.Fail("compiling", "EDITOR_COMPILING", "retry when not compiling", new { isCompiling = true });
            var json = JObject.Parse(CommandResult.FromOutcome("9", outcome).ToJson());
            Assert.Equal("retry when not compiling", (string)json["remediation"]);
            Assert.True((bool)json["details"]["isCompiling"]);
        }

        [Fact]
        public void ErrorWithoutRemediation_OmitsField()
        {
            var json = JObject.Parse(CommandResult.FromOutcome("9", HandlerOutcome.Fail("x", "INTERNAL_ERROR")).ToJson());
            Assert.Null(json["remediation"]);
            Assert.Null(json["details"]);
        }

        [Fact]
        public void NullOutcome_BecomesInternalError()
        {
            var json = JObject.Parse(CommandResult.FromOutcome("1", null).ToJson());
            Assert.Equal("error", (string)json["status"]);
            Assert.Equal("INTERNAL_ERROR", (string)json["code"]);
        }
    }
}
