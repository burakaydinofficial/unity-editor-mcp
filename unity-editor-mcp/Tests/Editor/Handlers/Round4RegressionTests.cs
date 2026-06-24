using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
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

        // round-6 bug #1: entering play mode in -batchmode freezes the bridge's update-loop command pump
        // UNRECOVERABLY (the editor process must be restarted). The handler must refuse in batchmode, not hang. CI
        // runs EditMode tests in -batchmode, so this asserts the guard in the exact hostile environment.
        [Test]
        public void PlayGame_InBatchMode_Refused()
        {
            if (!Application.isBatchMode) Assert.Ignore("only meaningful in -batchmode (a GUI editor can enter play mode safely)");
            var r = PlayModeHandler.HandleCommand("play_game", new JObject());
            Assert.IsTrue(r.IsError, "play_game must refuse in batchmode, not enter play mode");
            Assert.AreEqual("UNSUPPORTED_IN_BATCHMODE", r.Code);
            Assert.IsFalse(UnityEditor.EditorApplication.isPlaying, "must NOT have entered play mode");
        }

        // round-6 #3/#10: get_hierarchy must span ALL loaded scenes (additive/multi-scene), not just the active one,
        // and tag each root with its `scene`. (find_gameobject already spanned all loaded scenes — this aligns them.)
        [Test]
        public void GetHierarchy_SpansAllLoadedScenes()
        {
            var extra = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                UnityEditor.SceneManagement.NewSceneMode.Additive);
            try
            {
                var r = GameObjectHandler.GetHierarchy(new JObject());
                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);
                var loaded = (JArray)payload["loadedScenes"];
                Assert.GreaterOrEqual(loaded.Count, 2, "get_hierarchy must list ALL loaded scenes, not just the active one");
                foreach (var n in (JArray)payload["hierarchy"])
                    Assert.IsNotNull(n["scene"], "each top-level root must be tagged with its scene");
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.CloseScene(extra, true); }
        }

        // round-6 #6: manage_tools no longer fabricates packages (it used to inject a bogus "2D Sprite"/"Animation"
        // cache with a hardcoded "1.0.0" version). The built-in tools now report real versions; the fake cache is gone.
        [Test]
        public void ManageTools_NoFabricatedPackages()
        {
            var r = ToolManagementHandler.HandleCommand("get", new JObject());
            Assert.IsFalse(r.IsError, r.Error);
            var tools = (JArray)JObject.FromObject(r.Payload)["tools"];
            foreach (var t in tools)
            {
                var name = (string)t["name"];
                Assert.AreNotEqual("2D Sprite", name, "the fabricated '2D Sprite' entry must be gone");
                Assert.AreNotEqual("Animation", name, "the fabricated 'Animation' entry must be gone");
            }
        }

        // round-6 #9: set_active_scene changes which loaded scene is active (so creates can target a non-active scene).
        [Test]
        public void SetActiveScene_ChangesActiveScene()
        {
            var original = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var extra = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Additive);
            const string extraPath = "Assets/__sas_extra__.unity";
            try
            {
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(extra, extraPath);
                var r = SceneHandler.SetActiveScene(new JObject { ["scenePath"] = extraPath });
                Assert.IsFalse(r.IsError, r.Error);
                Assert.AreEqual(extraPath, UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path, "the extra scene must become active");
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.SetActiveScene(original);
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(extra, true);
                UnityEditor.AssetDatabase.DeleteAsset(extraPath);
            }
        }

        // round-6 #4: deleting a path that matches nothing is a NOT_FOUND error (audits ok:false), not a 0-count "success".
        [Test]
        public void DeleteGameObject_NothingFound_Errors()
        {
            var r = GameObjectHandler.DeleteGameObject(new JObject { ["path"] = "/__r6_no_such_object__" });
            Assert.IsTrue(r.IsError, "deleting a non-existent path must error, not 'succeed' with deletedCount:0");
            Assert.AreEqual("NOT_FOUND", r.Code);
        }

        // round-7 BUG 1: a componentType match must resolve to the COMPONENT's SerializedObject, not the GameObject's
        // (else the component's properties read as PROPERTY_NOT_FOUND unless you redundantly also pass `component`).
        [Test]
        public void SerializedMatch_ComponentType_ScopesToComponent()
        {
            var go = new GameObject("__r7_match__");
            go.AddComponent<Light>();
            try
            {
                var targets = SerializedTargeting.ResolveMatch(
                    new JObject { ["componentType"] = "Light" },
                    new JObject(), // no explicit `component`
                    10, out var code, out var msg);
                Assert.Greater(targets.Count, 0, msg ?? "should match the Light");
                Assert.IsTrue(targets.TrueForAll(t => t.Component is Light), "componentType match must scope to the Light component, not the GameObject");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // round-7 BUG 2: force:true must skip the compare-and-swap even when a (wrong) expected is supplied.
        [Test]
        public void SetSerializedProperties_Force_SkipsStaleExpected()
        {
            var go = new GameObject("__r7_force__");
            var light = go.AddComponent<Light>();
            light.intensity = 1f;
            try
            {
                var r = SerializedMemberHandler.Set(new JObject
                {
                    ["force"] = true,
                    ["edits"] = new JArray {
                        new JObject {
                            ["target"] = new JObject { ["instanceId"] = light.GetInstanceID() },
                            ["set"] = new JObject { ["m_Intensity"] = new JObject { ["value"] = 5f, ["expected"] = 999f } }
                        }
                    }
                });
                Assert.IsFalse(r.IsError, r.Error);
                Assert.AreEqual(5f, light.intensity, 0.001f, "force:true must skip the stale expected and apply the write");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // round-7 FR2: create_gameobject `components` array adds them (was silently dropped — only a Transform).
        [Test]
        public void CreateGameObject_ComponentsArray_AddsThem()
        {
            var r = GameObjectHandler.CreateGameObject(new JObject { ["name"] = "__r7_comps__", ["components"] = new JArray { "Light", "Rigidbody" } });
            try
            {
                Assert.IsFalse(r.IsError, r.Error);
                var go = GameObject.Find("__r7_comps__");
                Assert.IsNotNull(go);
                Assert.IsNotNull(go.GetComponent<Light>(), "Light from `components` must be added");
                Assert.IsNotNull(go.GetComponent<Rigidbody>(), "Rigidbody from `components` must be added");
            }
            finally { var g = GameObject.Find("__r7_comps__"); if (g != null) Object.DestroyImmediate(g); }
        }

#if UNITY_2021_2_OR_NEWER
        // Regression guards for the prefab-stage fixes (get_hierarchy + create_gameobject stage-awareness), verified
        // live on 2022.3. [UnityTest] + poll-until-current because OpenPrefab doesn't make the stage the CURRENT
        // prefab stage synchronously (it becomes current a tick later — a plain [Test] can't observe it, which is
        // why the first attempt failed). A prefab root takes the .prefab FILE name.
        [UnityTest]
        public IEnumerator PrefabStage_GetHierarchy_And_CreateInto()
        {
            var src = new GameObject("__ps_src__");
            const string path = "Assets/__ps_root__.prefab";
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(src, path);
            Object.DestroyImmediate(src);
            UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(path);
            for (int i = 0; i < 30 && UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() == null; i++)
                yield return null; // wait for the opened prefab to become the current stage
            try
            {
                // get_hierarchy surfaces the stage root (FILE name), not the main scene
                var h = GameObjectHandler.GetHierarchy(new JObject());
                Assert.IsFalse(h.IsError, h.Error);
                var hier = (JArray)JObject.FromObject(h.Payload)["hierarchy"];
                bool sawRoot = false;
                foreach (var n in hier) { if ((string)n["name"] == "__ps_root__") { sawRoot = true; break; } }
                Assert.IsTrue(sawRoot, "get_hierarchy must surface the open prefab stage's root");

                // create_gameobject with no parentPath + a stage open lands UNDER the prefab root
                var c = GameObjectHandler.CreateGameObject(new JObject { ["name"] = "__ps_child__" });
                Assert.IsFalse(c.IsError, c.Error);
                Assert.AreEqual("/__ps_root__/__ps_child__", (string)JObject.FromObject(c.Payload)["path"]);

                // create_gameobject with an explicit stage parentPath resolves (was NOT_FOUND)
                var cp = GameObjectHandler.CreateGameObject(new JObject { ["name"] = "__ps_bypath__", ["parentPath"] = "/__ps_root__" });
                Assert.IsFalse(cp.IsError, cp.Error);
                Assert.AreEqual("/__ps_root__/__ps_bypath__", (string)JObject.FromObject(cp.Payload)["path"]);

                // round-5 bug #1: the by-path resolvers (add_component / list_components / modify_gameobject) must
                // ALSO reach the open stage — they used to return NOT_FOUND on a stage path.
                var ac = ComponentHandler.AddComponent(new JObject { ["gameObjectPath"] = "/__ps_root__/__ps_child__", ["componentType"] = "Rigidbody" });
                Assert.IsFalse(ac.IsError, ac.Error);
                var lc = ComponentHandler.ListComponents(new JObject { ["gameObjectPath"] = "/__ps_root__/__ps_child__" });
                Assert.IsFalse(lc.IsError, lc.Error);
                var mg = GameObjectHandler.ModifyGameObject(new JObject { ["path"] = "/__ps_root__/__ps_child__", ["position"] = new JObject { ["x"] = 1f, ["y"] = 0f, ["z"] = 0f } });
                Assert.IsFalse(mg.IsError, mg.Error);

                // round-5 bug #2: save_prefab always succeeds on an explicit in-stage call (no misleading "No changes to save")
                var sp = AssetManagementHandler.SavePrefab(new JObject());
                Assert.IsFalse(sp.IsError, sp.Error);
                Assert.IsTrue((bool)JObject.FromObject(sp.Payload)["savedInPrefabMode"]);

                // code-review HIGH: a same-named main-scene object must NOT shadow the stage object — the stage wins.
                var decoy = new GameObject("__ps_root__"); // created in the main/active scene (same name as the stage root)
                try
                {
                    var resolved = GameObjectHandler.FindGameObjectStageAware("/__ps_root__");
                    Assert.AreNotSame(decoy, resolved, "the stage object must win over a same-named main-scene object");
                    Assert.AreEqual(AssetManagementHandler.GetOpenPrefabStageScene().Value, resolved.scene, "resolved object must be the one in the prefab stage");
                }
                finally { Object.DestroyImmediate(decoy); }
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
