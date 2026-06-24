using NUnit.Framework;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // Regression guards for the round-3 dogfood-found issues (the "silent wrong answer" class).
    public class Round3RegressionTests
    {
        // get_object_references reflected over C# fields and missed native serialized refs (Joint.m_ConnectedBody),
        // so it falsely reported objects as unreferenced. The SerializedObject scan must catch the component ref.
        [Test]
        public void GetObjectReferences_FindsComponentObjectReference()
        {
            var target = new GameObject("__ref_target__");
            var rb = target.AddComponent<Rigidbody>();
            var source = new GameObject("__ref_source__");
            var joint = source.AddComponent<HingeJoint>(); // auto-adds a Rigidbody to source
            // Wire connectedBody via SerializedObject so the serialized m_ConnectedBody is definitely committed
            // before the scan reads it — the C# setter's native->serialized sync isn't reliably synchronous on
            // 2022.3+ (the scan opens a fresh SerializedObject, which read null there).
            using (var so = new UnityEditor.SerializedObject(joint))
            {
                so.FindProperty("m_ConnectedBody").objectReferenceValue = rb;
                so.ApplyModifiedProperties();
            }
            try
            {
                var r = SceneAnalysisHandler.GetObjectReferences(new JObject { ["gameObjectName"] = "__ref_target__" });
                Assert.IsFalse(r.IsError, r.Error);
                var data = JObject.FromObject(r.Payload);
                Assert.Greater((int)data["stats"]["totalReferencedBy"], 0, "the HingeJoint.connectedBody reference should be detected");
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
            }
        }

        // clear_console's preserveWarnings/preserveErrors were a no-op placeholder that cleared everything anyway.
        // It must now refuse rather than silently destroy the errors the caller asked to keep.
        [Test]
        public void ClearConsole_PreserveFlags_Refused()
        {
            var r = ConsoleHandler.ClearConsole(new JObject { ["preserveErrors"] = true });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("VALIDATION_ERROR", r.Code);
        }

        // inspect_serialized_object pathPrefix returned [] for a leaf property (the round-2 subtree-scope fix's
        // edge case). A leaf prefix must emit that property itself.
        [Test]
        public void InspectSerializedObject_PathPrefix_LeafEmitsItself()
        {
            var go = new GameObject("__pp_leaf__");
            try
            {
                var r = SerializedMemberHandler.Inspect(new JObject
                {
                    ["target"] = new JObject { ["instanceId"] = go.GetInstanceID() },
                    ["component"] = "Transform",
                    ["pathPrefix"] = "m_LocalPosition.x" // a float leaf present on every floor (m_RootOrder isn't serialized on 2022.3+)
                });
                Assert.IsFalse(r.IsError, r.Error);
                var props = (JArray)JObject.FromObject(r.Payload)["object"]["properties"];
                Assert.AreEqual(1, props.Count);
                Assert.AreEqual("m_LocalPosition.x", (string)props[0]["propertyPath"]);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
