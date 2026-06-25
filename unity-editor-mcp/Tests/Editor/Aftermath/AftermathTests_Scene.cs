using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath tests for the Scene tool category (SceneHandler). Each test calls a handler, then
    /// INDEPENDENTLY re-reads Unity state through a DIFFERENT path (System.IO.File, SceneManager,
    /// EditorBuildSettings) and asserts the REAL effect happened AND that an unrelated baseline did not
    /// move. Every test restores all editor state (open scene, build settings, on-disk temp files) so it
    /// leaves zero residue.
    ///
    /// SKIPPED (no test written):
    ///   - create_scene: covered structurally elsewhere; a fully independent aftermath would duplicate the
    ///     save_scene + File.Exists assertion below. The interesting on-disk write path is exercised by
    ///     save_scene and by the temp-scene fixtures here.
    ///   - load_scene Single: replaces ALL open scenes — would unload the Unity Test Runner's own scene
    ///     context; reopening is exercised indirectly via the create-and-restore helpers, but a dedicated
    ///     Single-load test risks disturbing the runner. Additive load is exercised by close_scene's setup.
    ///   - set_active_scene: companion to close_scene; its effect (SceneManager.activeScene) is a one-line
    ///     mirror with no independent second source beyond GetActiveScene, so it is not separately aftermath-able.
    /// </summary>
    public class AftermathTests_Scene
    {
        private const string TempDir = "Assets/__AftermathSceneTmp__";

        private EditorBuildSettingsScene[] _originalBuildScenes;
        private string _originalScenePath;

        [SetUp]
        public void SetUp()
        {
            _originalBuildScenes = EditorBuildSettings.scenes;
            _originalScenePath = SceneManager.GetActiveScene().path;
            if (!AssetDatabase.IsValidFolder(TempDir))
            {
                AssetDatabase.CreateFolder("Assets", "__AftermathSceneTmp__");
            }
        }

        [TearDown]
        public void TearDown()
        {
            // Restore build settings (some tests mutate them).
            EditorBuildSettings.scenes = _originalBuildScenes;

            // Restore the originally-open scene context if it drifted (e.g. after an Additive/Single load).
            // Only reopen if we have a real saved original and it is no longer the single open scene.
            if (!string.IsNullOrEmpty(_originalScenePath) && File.Exists(_originalScenePath))
            {
                var active = SceneManager.GetActiveScene();
                if (active.path != _originalScenePath || SceneManager.sceneCount > 1)
                {
                    EditorSceneManager.OpenScene(_originalScenePath, OpenSceneMode.Single);
                }
            }
            else if (SceneManager.sceneCount > 1)
            {
                // No saved original to return to: collapse any additive scenes into a single empty one.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }

            // Remove the temp folder and everything we wrote.
            if (AssetDatabase.IsValidFolder(TempDir))
            {
                AssetDatabase.DeleteAsset(TempDir);
            }
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Creates a new empty scene on disk at the given temp path and returns the loaded Scene.
        /// Used to build independent fixtures without touching the runner's scene permanently.
        /// </summary>
        private Scene CreateTempSceneOnDisk(string fileName, NewSceneMode mode)
        {
            var path = TempDir + "/" + fileName + ".unity";
            // NewScene(Additive) refuses when the active scene is untitled+unsaved. If so, create + save a seed Single
            // scene first so the active has a path and the additive create is allowed (the seed lives under TempDir
            // and is removed with it in TearDown).
            if (mode == NewSceneMode.Additive && string.IsNullOrEmpty(SceneManager.GetActiveScene().path))
            {
                var seed = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(seed, TempDir + "/__seed_" + fileName + ".unity");
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            EditorSceneManager.SaveScene(scene, path);
            return scene;
        }

        // ---------------------------------------------------------------------------------------------
        // save_scene
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void SaveScene_WritesFileToDisk_AndClearsDirty()
        {
            // Arrange: a fresh single scene SAVED to a temp path once (so it HAS a path), then dirtied with a new
            // object. (A plain save persists the active scene to its own path; saveAs would make a copy WITHOUT
            // switching the active scene, so we test the in-place persist here.)
            var savePath = TempDir + "/SaveSceneTarget.unity";
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, savePath);
            Assert.IsTrue(File.Exists(savePath), "precondition: scene saved to disk once");
            long sizeBefore = new FileInfo(savePath).Length;

            var marker = new GameObject("AftermathSaveMarker");
            SceneManager.MoveGameObjectToScene(marker, SceneManager.GetActiveScene());
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty, "precondition: scene should be dirty before save");

            // Act: save the active scene to its current path (no saveAs).
            var r = SceneHandler.SaveScene(new JObject());

            // Assert: handler succeeded.
            Assert.IsFalse(r.IsError, r.Error);
            var payload = JObject.FromObject(r.Payload);
            Assert.IsTrue((bool)payload["saved"], "result.saved should be true");
            Assert.IsFalse((bool)payload["isDirty"], "result.isDirty should be false after save");

            // AFTERMATH (independent path #1 — SceneManager): the active scene is now clean, same path.
            var activeAfter = SceneManager.GetActiveScene();
            Assert.IsFalse(activeAfter.isDirty, "active scene should no longer be dirty");
            Assert.AreEqual(savePath, activeAfter.path, "in-place save must not change the active scene path");

            // AFTERMATH (independent path #2 — System.IO.File): the on-disk file grew (the marker was persisted).
            Assert.IsTrue(File.Exists(savePath), "scene file must still exist on disk");
            Assert.Greater(new FileInfo(savePath).Length, sizeBefore, "saved scene file should have grown (marker persisted)");
        }

        // ---------------------------------------------------------------------------------------------
        // close_scene
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void CloseScene_RemovesOneLoadedScene_LeavesOthers()
        {
            // Arrange: a base scene (Single) + a second scene loaded Additive, both saved on disk.
            CreateTempSceneOnDisk("CloseBase", NewSceneMode.Single);
            var additive = CreateTempSceneOnDisk("CloseExtra", NewSceneMode.Additive);
            var additivePath = additive.path;

            int countBefore = SceneManager.sceneCount;
            Assert.AreEqual(2, countBefore, "precondition: two scenes should be open");

            // Capture the baseline that must NOT change: the base scene must remain open.
            bool baseStillOpenBefore = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).path.EndsWith("CloseBase.unity")) baseStillOpenBefore = true;
            Assert.IsTrue(baseStillOpenBefore, "precondition: base scene should be open");

            // Act: close the additive scene by path.
            var r = SceneHandler.CloseScene(new JObject { ["scenePath"] = additivePath });

            // Assert: handler succeeded.
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH (independent path — SceneManager): the open-scene count dropped by exactly one,
            // the closed scene is gone, and the unrelated base scene is still open (baseline did not move).
            Assert.AreEqual(countBefore - 1, SceneManager.sceneCount, "exactly one scene should have closed");

            bool closedStillOpen = false;
            bool baseStillOpenAfter = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var p = SceneManager.GetSceneAt(i).path;
                if (p == additivePath) closedStillOpen = true;
                if (p.EndsWith("CloseBase.unity")) baseStillOpenAfter = true;
            }
            Assert.IsFalse(closedStillOpen, "closed scene should no longer be open");
            Assert.IsTrue(baseStillOpenAfter, "unrelated base scene must remain open");
        }

        // ---------------------------------------------------------------------------------------------
        // list_scenes
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void ListScenes_ReportsLoadedAndActiveScene_Correctly()
        {
            // Arrange: create + load a temp scene as the single active scene.
            var scene = CreateTempSceneOnDisk("ListTarget", NewSceneMode.Single);
            var scenePath = scene.path;

            // Cross-check the ground truth independently first.
            Assert.AreEqual(scenePath, SceneManager.GetActiveScene().path, "precondition: temp scene is active");

            // Act
            var r = SceneHandler.ListScenes(new JObject());

            // Assert: handler succeeded.
            Assert.IsFalse(r.IsError, r.Error);
            var payload = JObject.FromObject(r.Payload);
            var scenes = (JArray)payload["scenes"];
            Assert.IsNotNull(scenes);

            // AFTERMATH: locate our temp scene in the listing and assert its isLoaded/isActive flags match
            // the INDEPENDENT SceneManager ground truth (loaded=true, active=true), and that an unrelated
            // project scene that is NOT loaded is reported isLoaded=false / isActive=false.
            JObject listed = scenes
                .OfType<JObject>()
                .FirstOrDefault(s => (string)s["path"] == scenePath);
            Assert.IsNotNull(listed, "temp scene was not present in list_scenes output");
            Assert.IsTrue((bool)listed["isLoaded"], "temp scene should be reported as loaded");
            Assert.IsTrue((bool)listed["isActive"], "temp scene should be reported as active");

            foreach (var entry in scenes.OfType<JObject>())
            {
                var p = (string)entry["path"];
                if (p == scenePath) continue;
                // Any OTHER scene is not the active one — verify the handler did not falsely flag it active.
                bool actuallyActive = (p == SceneManager.GetActiveScene().path);
                Assert.AreEqual(actuallyActive, (bool)entry["isActive"],
                    "isActive flag disagrees with SceneManager for " + p);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // get_scene_info
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void GetSceneInfo_MatchesLiveActiveScene()
        {
            // Arrange: a known temp scene as the active scene.
            var scene = CreateTempSceneOnDisk("InfoTarget", NewSceneMode.Single);
            var scenePath = scene.path;
            var expectedName = scene.name;

            // Act: default (no identifier) -> info about the active scene.
            var r = SceneHandler.GetSceneInfo(new JObject());

            // Assert: handler succeeded.
            Assert.IsFalse(r.IsError, r.Error);
            var info = JObject.FromObject(r.Payload);

            // AFTERMATH (independent path — SceneManager + System.IO.File): every reported field matches
            // the live editor ground truth rather than the handler's own bookkeeping.
            var live = SceneManager.GetActiveScene();
            Assert.AreEqual(live.name, (string)info["sceneName"], "sceneName mismatch");
            Assert.AreEqual(scenePath, (string)info["scenePath"], "scenePath mismatch");
            Assert.AreEqual(live.isLoaded, (bool)info["isLoaded"], "isLoaded mismatch");
            Assert.IsTrue((bool)info["isActive"], "active scene should report isActive=true");
            Assert.AreEqual(expectedName, (string)info["sceneName"], "sceneName should equal the saved scene name");

            // The file exists, so a fileSize must be reported and match the real file length.
            Assert.IsTrue(File.Exists(scenePath), "precondition: scene file on disk");
            Assert.IsNotNull(info["fileSize"], "fileSize should be present for an on-disk scene");
            Assert.AreEqual(new FileInfo(scenePath).Length, (long)info["fileSize"], "fileSize mismatch vs disk");
        }

        // ---------------------------------------------------------------------------------------------
        // manage_build_settings (add / remove / move)
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void ManageBuildSettings_Add_AppendsSceneToEditorBuildSettings()
        {
            // Arrange: a real on-disk scene + a known baseline build list.
            CreateTempSceneOnDisk("BuildAdd", NewSceneMode.Additive);
            var addPath = TempDir + "/BuildAdd.unity";
            EditorBuildSettings.scenes = new EditorBuildSettingsScene[0];
            int countBefore = EditorBuildSettings.scenes.Length;

            // Act
            var r = SceneHandler.ManageBuildSettings(new JObject
            {
                ["action"] = "add",
                ["scenePath"] = addPath
            });

            // Assert: handler succeeded.
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH (independent path — EditorBuildSettings): membership changed by exactly one and the
            // added scene is present at the end (we appended, no index).
            var after = EditorBuildSettings.scenes;
            Assert.AreEqual(countBefore + 1, after.Length, "build settings count should grow by one");
            Assert.AreEqual(addPath, after[after.Length - 1].path, "added scene should be the last entry");
            Assert.IsTrue(after.Any(s => s.path == addPath), "added scene must be a member of build settings");
        }

        [Test]
        public void ManageBuildSettings_Remove_DropsSceneFromEditorBuildSettings()
        {
            // Arrange: two on-disk scenes seeded directly into build settings (independent of the handler).
            CreateTempSceneOnDisk("BuildRemoveA", NewSceneMode.Additive);
            CreateTempSceneOnDisk("BuildRemoveB", NewSceneMode.Additive);
            var aPath = TempDir + "/BuildRemoveA.unity";
            var bPath = TempDir + "/BuildRemoveB.unity";
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(aPath, true),
                new EditorBuildSettingsScene(bPath, true)
            };

            // Act: remove the first one by path.
            var r = SceneHandler.ManageBuildSettings(new JObject
            {
                ["action"] = "remove",
                ["scenePath"] = aPath
            });

            // Assert: handler succeeded.
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH (independent path — EditorBuildSettings): A is gone, B (the unrelated baseline entry)
            // is untouched.
            var after = EditorBuildSettings.scenes;
            Assert.AreEqual(1, after.Length, "exactly one scene should remain");
            Assert.IsFalse(after.Any(s => s.path == aPath), "removed scene must be gone");
            Assert.IsTrue(after.Any(s => s.path == bPath), "unrelated scene must remain");
        }

        [Test]
        public void ManageBuildSettings_Move_ReordersEditorBuildSettings()
        {
            // Arrange: three on-disk scenes seeded into build settings in a known order.
            CreateTempSceneOnDisk("BuildMove0", NewSceneMode.Additive);
            CreateTempSceneOnDisk("BuildMove1", NewSceneMode.Additive);
            CreateTempSceneOnDisk("BuildMove2", NewSceneMode.Additive);
            var p0 = TempDir + "/BuildMove0.unity";
            var p1 = TempDir + "/BuildMove1.unity";
            var p2 = TempDir + "/BuildMove2.unity";
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(p0, true),
                new EditorBuildSettingsScene(p1, true),
                new EditorBuildSettingsScene(p2, true)
            };

            // Act: move index 0 -> index 2.
            var r = SceneHandler.ManageBuildSettings(new JObject
            {
                ["action"] = "move",
                ["index"] = 0,
                ["toIndex"] = 2
            });

            // Assert: handler succeeded.
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH (independent path — EditorBuildSettings): the order is now [p1, p2, p0]; the set of
            // members (baseline) is unchanged — only ORDER moved.
            var after = EditorBuildSettings.scenes;
            Assert.AreEqual(3, after.Length, "count must be unchanged by a move");
            Assert.AreEqual(p1, after[0].path, "index 0 should now be the former index 1");
            Assert.AreEqual(p2, after[1].path, "index 1 should now be the former index 2");
            Assert.AreEqual(p0, after[2].path, "moved scene should be at the end");

            var paths = after.Select(s => s.path).OrderBy(x => x).ToArray();
            var expected = new[] { p0, p1, p2 }.OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(expected, paths, "membership must be preserved across a move");
        }
    }
}
