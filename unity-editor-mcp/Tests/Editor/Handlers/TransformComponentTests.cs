using NUnit.Framework;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    public class TransformComponentTests
    {
        // ---- F3: transform space ----

        [Test] public void ModifyGameObject_LocalSpace_SetsLocalPosition()
        {
            var parent = new GameObject("TcParent"); parent.transform.position = new Vector3(10, 0, 0);
            var child = new GameObject("TcChild"); child.transform.SetParent(parent.transform);
            try
            {
                var r = GameObjectHandler.ModifyGameObject(new JObject { ["path"] = "/TcParent/TcChild", ["space"] = "local", ["position"] = new JObject { ["x"] = 5, ["y"] = 0, ["z"] = 0 } });
                Assert.IsFalse(r.IsError, r.Error);
                Assert.AreEqual(5f, child.transform.localPosition.x, 0.001f);  // local set
                Assert.AreEqual(15f, child.transform.position.x, 0.001f);      // world = parent(10) + local(5)
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test] public void ModifyGameObject_WorldSpace_Default_SetsWorldPosition()
        {
            var parent = new GameObject("TcParent2"); parent.transform.position = new Vector3(10, 0, 0);
            var child = new GameObject("TcChild2"); child.transform.SetParent(parent.transform);
            try
            {
                var r = GameObjectHandler.ModifyGameObject(new JObject { ["path"] = "/TcParent2/TcChild2", ["position"] = new JObject { ["x"] = 5, ["y"] = 0, ["z"] = 0 } });
                Assert.IsFalse(r.IsError, r.Error);
                Assert.AreEqual(5f, child.transform.position.x, 0.001f);       // world set (default)
            }
            finally { Object.DestroyImmediate(parent); }
        }

        // ---- F4: reorder ----

        [Test] public void ReorderComponent_MovesUp_ChangesOrder()
        {
            var go = new GameObject("TcReorder");
            go.AddComponent<BoxCollider>();
            go.AddComponent<SphereCollider>(); // Transform, BoxCollider, SphereCollider
            try
            {
                var r = ComponentHandler.ReorderComponent(new JObject { ["gameObjectPath"] = "/TcReorder", ["componentType"] = "SphereCollider", ["direction"] = "up", ["count"] = 1 });
                Assert.IsFalse(r.IsError, r.Error);
                Assert.AreEqual(1, (int)JObject.FromObject(r.Payload)["moved"]);
                var comps = go.GetComponents<Component>();
                int sphere = System.Array.FindIndex(comps, c => c is SphereCollider);
                int box = System.Array.FindIndex(comps, c => c is BoxCollider);
                Assert.Less(sphere, box); // SphereCollider now precedes BoxCollider
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ---- F4: RequireComponent-aware remove ----

        [Test] public void RemoveComponent_RequiredByAnother_RefusedWithCode()
        {
            var go = new GameObject("TcRequire");
            go.AddComponent<ConstantForce>(); // [RequireComponent(typeof(Rigidbody))] -> Rigidbody auto-added
            try
            {
                Assert.IsNotNull(go.GetComponent<Rigidbody>(), "Rigidbody auto-added by RequireComponent");
                var r = ComponentHandler.RemoveComponent(new JObject { ["gameObjectPath"] = "/TcRequire", ["componentType"] = "Rigidbody" });
                Assert.IsTrue(r.IsError);
                Assert.AreEqual("COMPONENT_REQUIRED", r.Code);
                Assert.IsNotNull(go.GetComponent<Rigidbody>(), "Rigidbody NOT removed (no false success)");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
