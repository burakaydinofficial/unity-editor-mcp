using NUnit.Framework;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // Regression guards for the round-2 dogfood-found issues.
    public class Round2RegressionTests
    {
        // find_implementations dumped every implementor (772/105KB for ScriptableObject). It must cap + signal.
        [Test]
        public void FindImplementations_Limit_CapsAndTruncates()
        {
            var r = CodeIntelligenceHandler.FindImplementations(new JObject { ["typeName"] = "UnityEngine.MonoBehaviour", ["limit"] = 5 });
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);
            Assert.LessOrEqual((int)data["count"], 5);
            Assert.GreaterOrEqual((int)data["total"], (int)data["count"]);
            if ((int)data["total"] > 5) Assert.IsTrue((bool)data["truncated"]);
        }

        // simulate_ui_input simple mode ({elementPath, inputType}) was rejected with "inputSequence is required".
        // Post-fix it builds a one-action sequence and runs it (a bogus path errors per-action, not at the handler).
        [Test]
        public void SimulateUIInput_SimpleMode_NotRejected()
        {
            var r = UIInteractionHandler.SimulateUIInput(new JObject { ["elementPath"] = "/__no_such_ui__", ["inputType"] = "click" });
            Assert.IsFalse(r.IsError, r.Error);
        }

        // inspect_serialized_object pathPrefix leaked sibling properties; it must stay within the prefix subtree.
        [Test]
        public void InspectSerializedObject_PathPrefix_ScopesToSubtree()
        {
            var go = new GameObject("__pp_reg__");
            try
            {
                var r = SerializedMemberHandler.Inspect(new JObject
                {
                    ["target"] = new JObject { ["instanceId"] = go.GetInstanceID() },
                    ["component"] = "Transform",
                    ["pathPrefix"] = "m_LocalPosition"
                });
                Assert.IsFalse(r.IsError, r.Error);
                var props = (JArray)JObject.FromObject(r.Payload)["object"]["properties"];
                Assert.Greater(props.Count, 0);
                foreach (var p in props)
                    Assert.IsTrue(((string)p["propertyPath"]).StartsWith("m_LocalPosition"),
                        $"pathPrefix leaked a sibling: {p["propertyPath"]}");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // close_scene error paths (the happy path — additive load + selective close — is dogfooded live).
        [Test]
        public void CloseScene_MissingTarget_ValidationError()
        {
            var r = SceneHandler.CloseScene(new JObject());
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("VALIDATION_ERROR", r.Code);
        }

        [Test]
        public void CloseScene_NotLoaded_NotFound()
        {
            var r = SceneHandler.CloseScene(new JObject { ["sceneName"] = "__no_such_scene_xyz__" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("NOT_FOUND", r.Code);
        }
    }
}
