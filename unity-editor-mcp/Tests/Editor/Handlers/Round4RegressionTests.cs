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

        // modify_prefab reported success + modifiedProperties:["name"] for a rename that did nothing — a prefab
        // root's name follows the .prefab file name (SaveAsPrefabAsset re-derives it). It must reject, not false-succeed.
        [Test]
        public void ModifyPrefab_Rename_RejectedNotFalseSuccess()
        {
            var src = new GameObject("__r5_prefab_src__");
            const string path = "Assets/__r5_modprefab__.prefab";
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(src, path);
            Object.DestroyImmediate(src);
            try
            {
                var r = AssetManagementHandler.ModifyPrefab(new JObject
                {
                    ["prefabPath"] = path,
                    ["modifications"] = new JObject { ["name"] = "__r5_renamed__" }
                });
                Assert.IsTrue(r.IsError, "renaming via modify_prefab must be rejected, not false-succeed");
                Assert.AreEqual("VALIDATION_ERROR", r.Code);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(path); }
        }

        // modify_material silently dropped a color given as an object {r,g,b,a} into propertiesFailed (only the
        // array form [r,g,b,a] was handled). Both forms must apply.
        [Test]
        public void ModifyMaterial_ColorAsObject_Applied()
        {
            const string matPath = "Assets/__r6_mat__.mat";
            UnityEditor.AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), matPath);
            try
            {
                var r = AssetManagementHandler.ModifyMaterial(new JObject
                {
                    ["materialPath"] = matPath,
                    ["properties"] = new JObject { ["_Color"] = new JObject { ["r"] = 1f, ["g"] = 0f, ["b"] = 0f, ["a"] = 1f } }
                });
                Assert.IsFalse(r.IsError, r.Error);
                var failed = (JArray)JObject.FromObject(r.Payload)["propertiesFailed"];
                Assert.AreEqual(0, failed.Count, "object-form {r,g,b,a} color must apply, not land in propertiesFailed");
                Assert.AreEqual(Color.red, UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(matPath).GetColor("_Color"));
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(matPath); }
        }

        // read_logs read the LogCapture buffer (reset every domain reload → ~1 entry); it now reads the editor
        // console (LogEntries), like enhanced_read_logs. A freshly-logged warning must appear.
        [Test]
        public void ReadLogs_SurfacesEditorConsole()
        {
            var marker = "R7_readlogs_" + System.Guid.NewGuid().ToString("N");
            Debug.LogWarning(marker);
            var r = SystemHandler.ReadLogs(new JObject { ["count"] = 500 });
            Assert.IsFalse(r.IsError, r.Error);
            var logs = (JArray)JObject.FromObject(r.Payload)["logs"];
            bool found = false;
            foreach (var l in logs) { if (((string)l["message"] ?? "").Contains(marker)) { found = true; break; } }
            Assert.IsTrue(found, "read_logs must surface the editor console (it should contain the freshly-logged marker)");
        }

#if UNITY_2021_2_OR_NEWER
        // get_hierarchy read only the active (main) scene, so an open prefab stage's contents were invisible. With
        // a prefab open in stage mode, get_hierarchy must surface the stage root. (Synchronous OpenPrefab is
        // 2021.2+, so this test is guarded; the fix itself is floor-safe via GetCurrentPrefabStage.)
        [Test]
        public void GetHierarchy_SeesOpenPrefabStage()
        {
            var src = new GameObject("__r8_stageroot__");
            const string path = "Assets/__r8_stage__.prefab";
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(src, path);
            Object.DestroyImmediate(src);
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(path);
            try
            {
                Assert.IsNotNull(stage, "prefab stage should open");
                var r = GameObjectHandler.GetHierarchy(new JObject());
                Assert.IsFalse(r.IsError, r.Error);
                var hier = (JArray)JObject.FromObject(r.Payload)["hierarchy"];
                bool found = false;
                foreach (var n in hier) { if ((string)n["name"] == "__r8_stageroot__") { found = true; break; } }
                Assert.IsTrue(found, "get_hierarchy must surface the open prefab stage's root");
            }
            finally
            {
                UnityEditor.SceneManagement.StageUtility.GoBackToPreviousStage();
                UnityEditor.AssetDatabase.DeleteAsset(path);
            }
        }
#endif
    }
}
