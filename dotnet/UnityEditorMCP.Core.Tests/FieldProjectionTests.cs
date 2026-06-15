using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class FieldProjectionTests
    {
        private static List<string> F(params string[] s) => new List<string>(s);

        // ---- pure projection ----

        [Fact]
        public void TopLevelSelection_KeepsOnlySelectedKeys()
        {
            var payload = JObject.Parse(@"{""a"":1,""b"":2,""c"":3}");
            var r = (JObject)FieldProjection.Project(payload, F("a", "c"));
            Assert.Equal(1, (int)r["a"]);
            Assert.Equal(3, (int)r["c"]);
            Assert.Null(r["b"]);
        }

        [Fact]
        public void NestedDotPath_DescendsIntoObject()
        {
            var payload = JObject.Parse(@"{""state"":{""isPlaying"":true,""isPaused"":false},""x"":9}");
            var r = (JObject)FieldProjection.Project(payload, F("state.isPlaying"));
            Assert.True((bool)r["state"]["isPlaying"]);
            Assert.Null(r["state"]["isPaused"]);
            Assert.Null(r["x"]);
        }

        [Fact]
        public void ArrayIsTransparent_ProjectsEachElement()
        {
            var payload = JObject.Parse(@"{""count"":2,""objects"":[{""name"":""A"",""tag"":""t1""},{""name"":""B"",""tag"":""t2""}]}");
            var r = (JObject)FieldProjection.Project(payload, F("count", "objects.name"));
            Assert.Equal(2, (int)r["count"]);
            var arr = (JArray)r["objects"];
            Assert.Equal(2, arr.Count);
            Assert.Equal("A", (string)arr[0]["name"]);
            Assert.Null(arr[0]["tag"]);
            Assert.Equal("B", (string)arr[1]["name"]);
        }

        [Fact]
        public void LeafSelection_WinsOverDeeperPath()
        {
            var payload = JObject.Parse(@"{""obj"":{""a"":1,""b"":2}}");
            var r = (JObject)FieldProjection.Project(payload, F("obj", "obj.a")); // leaf "obj" wins → whole subtree
            Assert.Equal(1, (int)r["obj"]["a"]);
            Assert.Equal(2, (int)r["obj"]["b"]);
        }

        [Fact]
        public void MissingPath_IsSilentlyOmitted()
        {
            var payload = JObject.Parse(@"{""a"":1}");
            var r = (JObject)FieldProjection.Project(payload, F("a", "nope.deep"));
            Assert.Equal(1, (int)r["a"]);
            Assert.Null(r["nope"]);
        }

        [Fact]
        public void EmptyOrNullPaths_ReturnPayloadUnchanged()
        {
            var payload = JObject.Parse(@"{""a"":1,""b"":2}");
            Assert.Same(payload, FieldProjection.Project(payload, new List<string>()));
            Assert.Same(payload, FieldProjection.Project(payload, null));
        }

        // ---- dispatcher integration ----

        [Fact]
        public void Dispatcher_ProjectsSuccessPayload_AndStripsMetaParamFromHandler()
        {
            JObject seenByHandler = null;
            var d = new CommandDispatcher();
            d.Register("get", p => { seenByHandler = p; return HandlerOutcome.Ok(new JObject { ["a"] = 1, ["b"] = 2 }); });
            var req = new CommandRequest { Id = "1", Type = "get", Params = JObject.Parse(@"{""fields"":[""a""]}") };
            var json = JObject.Parse(d.Dispatch(req).ToJson());
            Assert.Equal(1, (int)json["result"]["a"]);
            Assert.Null(json["result"]["b"]);
            Assert.NotNull(seenByHandler);
            Assert.Null(seenByHandler["fields"]); // handler never sees the meta-param
        }

        [Fact]
        public void Dispatcher_DoesNotProjectErrors()
        {
            var d = new CommandDispatcher();
            d.Register("boom", p => HandlerOutcome.Fail("bad input", "VALIDATION_ERROR"));
            var req = new CommandRequest { Id = "2", Type = "boom", Params = JObject.Parse(@"{""fields"":[""whatever""]}") };
            var json = JObject.Parse(d.Dispatch(req).ToJson());
            Assert.Equal("error", (string)json["status"]);
            Assert.Equal("bad input", (string)json["error"]);
            Assert.Null(json["result"]);
        }

        [Fact]
        public void Dispatcher_NoFields_ReturnsFullPayload()
        {
            var d = new CommandDispatcher();
            d.Register("get", p => HandlerOutcome.Ok(new JObject { ["a"] = 1, ["b"] = 2 }));
            var json = JObject.Parse(d.Dispatch(new CommandRequest { Id = "3", Type = "get" }).ToJson());
            Assert.Equal(1, (int)json["result"]["a"]);
            Assert.Equal(2, (int)json["result"]["b"]);
        }

        [Fact]
        public void Dispatcher_ProjectsAnonymousObjectPayload()
        {
            // Most handlers return an anonymous POCO, not a JObject — projection must still apply.
            var d = new CommandDispatcher();
            d.Register("get", p => HandlerOutcome.Ok(new { name = "x", tag = "y" }));
            var req = new CommandRequest { Id = "4", Type = "get", Params = JObject.Parse(@"{""fields"":[""name""]}") };
            var json = JObject.Parse(d.Dispatch(req).ToJson());
            Assert.Equal("x", (string)json["result"]["name"]);
            Assert.Null(json["result"]["tag"]);
        }
    }
}
