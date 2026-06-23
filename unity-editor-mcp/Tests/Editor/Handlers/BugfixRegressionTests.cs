using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // Regression guards for the 0.20.1 dogfood-found bugs. Each reproduces the failing call and asserts the fix.
    public class BugfixRegressionTests
    {
        // #10: get_component_values choked on cyclic Unity graphs (Matrix4x4.rotation.eulerAngles.normalized) —
        // JObject.FromObject below throws "Self referencing loop" if SerializeValue returns the raw value.
        [Test]
        public void GetComponentValues_BuiltInComponent_DoesNotLoop()
        {
            var go = new GameObject("__gcv_reg__");
            try
            {
                var r = SceneAnalysisHandler.GetComponentValues(new JObject
                {
                    ["gameObjectName"] = "__gcv_reg__",
                    ["componentType"] = "Transform"
                });
                Assert.IsFalse(r.IsError, r.Error);
                var data = JObject.FromObject(r.Payload); // would throw on the cyclic graph pre-fix
                Assert.IsNotNull(data["properties"]);
                Assert.IsNotNull(data["properties"]["worldToLocalMatrix"]); // the loop trigger — now a safe string
            }
            finally { Object.DestroyImmediate(go); }
        }

        // #11: find_assets had no cap (an unscoped query returned everything). limit must cap + signal truncation.
        [Test]
        public void FindAssets_Limit_CapsAndSignalsTruncation()
        {
            var r = AssetDatabaseHandler.HandleCommand("find_assets", new JObject { ["filter"] = "t:MonoScript", ["limit"] = 1 });
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);
            Assert.LessOrEqual((int)data["count"], 1);
            Assert.GreaterOrEqual((int)data["total"], (int)data["count"]);
            if ((int)data["total"] > 1) Assert.IsTrue((bool)data["truncated"]);
        }

        // #12: modify_gameobject with space:local + a reparent stored the local value relative to the OLD parent
        // (reparent uses worldPositionStays). Local must end up relative to the NEW parent, and the echo must
        // surface localPosition.
        [Test]
        public void ModifyGameObject_ReparentLocalSpace_CorrectLocalAndEcho()
        {
            var a = new GameObject("__mg_A__");
            a.transform.position = new Vector3(10, 0, 0);
            var b = new GameObject("__mg_B__");
            try
            {
                var r = GameObjectHandler.ModifyGameObject(new JObject
                {
                    ["path"] = "/__mg_B__",
                    ["parentPath"] = "/__mg_A__",
                    ["space"] = "local",
                    ["position"] = new JObject { ["x"] = 0, ["y"] = 5, ["z"] = 0 }
                });
                Assert.IsFalse(r.IsError, r.Error);
                Assert.AreEqual(0f, b.transform.localPosition.x, 0.001f);
                Assert.AreEqual(5f, b.transform.localPosition.y, 0.001f);
                Assert.AreEqual(10f, b.transform.position.x, 0.001f); // world = parent(10) + local(0)
                Assert.IsNotNull(JObject.FromObject(r.Payload)["localPosition"]);
            }
            finally
            {
                Object.DestroyImmediate(a);
                if (b != null) Object.DestroyImmediate(b);
            }
        }

        // #14: instantiate_prefab set name/position on the instance but never recorded them as overrides, so a
        // later prefab sync (reverting an unrelated property) discarded them. They must be recorded.
        [Test]
        public void InstantiatePrefab_RecordsNameAndPositionOverrides()
        {
            var src = new GameObject("__ip_src__");
            const string prefabPath = "Assets/__ip_reg__.prefab";
            PrefabUtility.SaveAsPrefabAsset(src, prefabPath);
            Object.DestroyImmediate(src);
            try
            {
                var r = AssetManagementHandler.InstantiatePrefab(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["name"] = "__ip_clone__",
                    ["position"] = new JObject { ["x"] = -3, ["y"] = 1, ["z"] = 0 }
                });
                Assert.IsFalse(r.IsError, r.Error);
                var instance = GameObject.Find("__ip_clone__");
                Assert.IsNotNull(instance);
                try
                {
                    var mods = PrefabUtility.GetPropertyModifications(instance) ?? new PropertyModification[0];
                    Assert.IsTrue(mods.Any(m => m.propertyPath == "m_Name" && m.value == "__ip_clone__"), "name override recorded");
                    Assert.IsTrue(mods.Any(m => m.propertyPath == "m_LocalPosition.x"), "position override recorded");
                }
                finally { Object.DestroyImmediate(instance); }
            }
            finally { AssetDatabase.DeleteAsset(prefabPath); }
        }
    }
}
