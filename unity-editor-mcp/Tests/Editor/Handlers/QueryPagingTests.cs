using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // F2: the heavy query tools must cap their RESPONSE so a big legacy scene can't blow the 1MB frame budget.
    public class QueryPagingTests
    {
        private readonly List<GameObject> _gos = new List<GameObject>();
        [TearDown] public void Teardown() { foreach (var g in _gos) if (g != null) Object.DestroyImmediate(g); _gos.Clear(); }

        private void Make(int n, string name, System.Type comp = null)
        {
            for (int i = 0; i < n; i++) _gos.Add(comp == null ? new GameObject(name) : new GameObject(name, comp));
        }

        [Test] public void FindGameObjects_OverLimit_TruncatesAndSignals()
        {
            Make(5, "PageFixtureA");
            var data = JObject.FromObject(GameObjectHandler.FindGameObjects(new JObject { ["name"] = "PageFixtureA", ["limit"] = 2 }).Payload);
            Assert.AreEqual(2, (int)data["count"]);                 // capped
            Assert.AreEqual(5, (int)data["total"]);                 // true total preserved
            Assert.IsTrue((bool)data["truncated"]);
            Assert.AreEqual(2, ((JArray)data["objects"]).Count);
        }

        [Test] public void FindGameObjects_UnderLimit_NotTruncated()
        {
            Make(3, "PageFixtureB");
            var data = JObject.FromObject(GameObjectHandler.FindGameObjects(new JObject { ["name"] = "PageFixtureB", ["limit"] = 10 }).Payload);
            Assert.AreEqual(3, (int)data["count"]);
            Assert.IsFalse((bool)data["truncated"]);
        }

        [Test] public void GetHierarchy_OverMaxNodes_TruncatesAndCaps()
        {
            Make(6, "HierFixture"); // 6 fixture roots + whatever the scene already has > maxNodes 3
            var data = JObject.FromObject(GameObjectHandler.GetHierarchy(new JObject { ["maxNodes"] = 3 }).Payload);
            Assert.IsTrue((bool)data["truncated"]);
            Assert.LessOrEqual((int)data["objectCount"], 3); // emitted node count bounded by the budget
        }

        [Test] public void FindByComponent_OverLimit_TruncatesAndSignals()
        {
            Make(5, "FbcFixture", typeof(BoxCollider));
            var data = JObject.FromObject(SceneAnalysisHandler.FindByComponent(new JObject { ["componentType"] = "BoxCollider", ["searchScope"] = "scene", ["limit"] = 2 }).Payload);
            Assert.AreEqual(2, ((JArray)data["results"]).Count);     // response capped
            Assert.IsTrue((bool)data["truncated"]);
            Assert.GreaterOrEqual((int)data["totalFound"], 5);       // true total preserved
        }
    }
}
