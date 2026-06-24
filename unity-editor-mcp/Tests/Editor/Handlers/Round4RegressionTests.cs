using NUnit.Framework;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // Regression guards for the round-4 dogfood-found issues (the "silent false-success / ignored-filter" class).
    public class Round4RegressionTests
    {
        // modify_gameobject swallowed the "Tag not defined" throw and still reported success (the object stayed
        // Untagged). It must now fail honestly instead of false-succeeding.
        [Test]
        public void ModifyGameObject_UndefinedTag_Fails()
        {
            var go = new GameObject("__r4_tag__");
            try
            {
                var r = GameObjectHandler.ModifyGameObject(new JObject
                {
                    ["path"] = "/__r4_tag__",
                    ["tag"] = "__r4_no_such_tag__"
                });
                Assert.IsTrue(r.IsError, "an undefined tag must fail, not silently false-succeed");
                Assert.AreEqual("VALIDATION_ERROR", r.Code);
                Assert.AreEqual("Untagged", go.tag); // unchanged
            }
            finally { Object.DestroyImmediate(go); }
        }

        // get_component_types reads its name filter from `search`, but callers reasonably pass `searchTerm`/`query`
        // (the result even echoes it as `searchTerm`). Those aliases must filter, not be silently ignored.
        [Test]
        public void GetComponentTypes_SearchAlias_Filters()
        {
            var all = ComponentHandler.GetComponentTypes(new JObject());
            Assert.IsFalse(all.IsError, all.Error);
            int total = (int)JObject.FromObject(all.Payload)["totalCount"];

            var r = ComponentHandler.GetComponentTypes(new JObject { ["searchTerm"] = "Collider" });
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);
            int filtered = (int)data["totalCount"];
            Assert.Greater(filtered, 0, "the alias should still match the Collider types");
            Assert.Less(filtered, total, "searchTerm alias must actually filter the list");
            foreach (var t in (JArray)data["componentTypes"])
                StringAssert.Contains("Collider", (string)t["name"]);
        }

        // enhanced_read_logs computed its statistics over the WHOLE buffer regardless of the filter (a misleading
        // global tally). With a unique filterText that matches nothing, the filtered set is empty, so every stat
        // must be 0 — pre-fix these reflected the whole buffer (non-zero whenever any log exists). Also exercises
        // the `logType` (singular) alias, which used to be silently ignored.
        [Test]
        public void EnhancedReadLogs_StatisticsScopedToFilter()
        {
            var marker = "R4_no_log_matches_this_" + System.Guid.NewGuid().ToString("N");
            var r = ConsoleHandler.EnhancedReadLogs(new JObject
            {
                ["logType"] = "Warning",
                ["filterText"] = marker,
                ["count"] = 100
            });
            Assert.IsFalse(r.IsError, r.Error);
            var stats = JObject.FromObject(r.Payload)["statistics"];
            Assert.AreEqual(0, (int)stats["errors"], "statistics must be scoped to the filtered set, not the whole buffer");
            Assert.AreEqual(0, (int)stats["logs"], "statistics must be scoped to the filtered set, not the whole buffer");
            Assert.AreEqual(0, (int)stats["warnings"], "statistics must be scoped to the filtered set, not the whole buffer");
        }
    }
}
