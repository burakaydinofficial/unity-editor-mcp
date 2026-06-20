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

        internal static JToken FindProp(JArray props, string path)
        {
            foreach (var p in props) if ((string)p["propertyPath"] == path) return p;
            return null;
        }
    }
}
