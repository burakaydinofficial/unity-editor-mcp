using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    public class SerializedMemberHandlerTests
    {
        private string _assetPath;
        private SerFixtureAsset _asset;
        [SetUp] public void Setup()
        {
            _asset = ScriptableObject.CreateInstance<SerFixtureAsset>();
            _assetPath = "Assets/__sertest__.asset";
            AssetDatabase.CreateAsset(_asset, _assetPath);
        }
        [TearDown] public void Teardown() { AssetDatabase.DeleteAsset(_assetPath); }

        [Test] public void Inspect_AssetByPath_ReturnsTreeWithValuesAndTypes()
        {
            var outcome = SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath } });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            var props = (JArray)data["object"]["properties"];
            var intField = FindProp(props, "IntField");
            Assert.IsNotNull(intField);
            Assert.AreEqual("Integer", (string)intField["propertyType"]);
            Assert.AreEqual(7L, (long)intField["value"]);
            Assert.IsNotNull(FindProp(props, "privateFloat"), "private [SerializeField] must appear");
        }

        [Test] public void Set_PrivateSerializeField_Writes_Headline()
        {
            var read = SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath } });
            var props = (JArray)JObject.FromObject(read.Payload)["object"]["properties"];
            var cur = FindProp(props, "privateFloat")["value"];
            var outcome = SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["privateFloat"] = new JObject { ["value"] = 42.0, ["expected"] = cur } } } } });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            Assert.AreEqual(42f, _asset.ReadPrivateFloat(), 0.0001f); // the private field actually changed
        }

        [Test] public void Set_StaleExpected_SkipsWithStale()
        {
            var outcome = SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 99, ["expected"] = 12345 } } } } }); // wrong expected
            var data = JObject.FromObject(outcome.Payload);
            Assert.AreEqual(0, ((JArray)data["changed"]).Count);
            Assert.AreEqual("STALE", (string)((JArray)data["skipped"])[0]["code"]);
            Assert.AreEqual(7, _asset.IntField); // unchanged
        }

        [Test] public void Set_MissingExpected_RefusedUnlessForce()
        {
            var noForce = SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 5 } } } } });
            Assert.AreEqual("MISSING_PRECONDITION", (string)((JArray)JObject.FromObject(noForce.Payload)["skipped"])[0]["code"]);
            var forced = SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 5 } } } } });
            Assert.IsFalse(forced.IsError, forced.Error);
            Assert.AreEqual(5, _asset.IntField);
        }

        internal static JToken FindProp(JArray props, string path)
        {
            foreach (var p in props) if ((string)p["propertyPath"] == path) return p;
            return null;
        }
    }
}
