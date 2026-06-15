using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorMCP.Handlers;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Tests
{
    [TestFixture]
    public class SceneHandlerTests
    {
        private string testSceneFolder = "Assets/TestScenes";
        // The minimal-parameters case creates here (the handler's default), OUTSIDE
        // testSceneFolder — so it must be cleaned explicitly or it "already exists".
        private const string DefaultScenePath = "Assets/Scenes/TestScene.unity";

        // Handlers return anonymous objects (internal to the Editor assembly), so
        // assert against a JObject view rather than `dynamic`: a missing key reads
        // as null instead of throwing, and it works across the assembly boundary.
        private static JObject Create(JObject parameters)
            => TestHelpers.Result(SceneHandler.CreateScene(parameters));

        [SetUp]
        public void Setup()
        {
            if (!AssetDatabase.IsValidFolder(testSceneFolder))
            {
                AssetDatabase.CreateFolder("Assets", "TestScenes");
            }
            // Remove any leftover from a prior run so creation doesn't fail as "exists".
            if (File.Exists(DefaultScenePath))
            {
                AssetDatabase.DeleteAsset(DefaultScenePath);
            }
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test scenes
            if (AssetDatabase.IsValidFolder(testSceneFolder))
            {
                AssetDatabase.DeleteAsset(testSceneFolder);
            }
            if (File.Exists(DefaultScenePath))
            {
                AssetDatabase.DeleteAsset(DefaultScenePath);
            }

            // Remove any test scenes from build settings
            var buildScenes = EditorBuildSettings.scenes.ToList();
            buildScenes.RemoveAll(s => s.path.Contains("TestScene"));
            EditorBuildSettings.scenes = buildScenes.ToArray();
        }

        [Test]
        public void CreateScene_ShouldWorkWithMinimalParameters()
        {
            var parameters = new JObject
            {
                ["sceneName"] = "TestScene"
            };

            var result = Create(parameters);

            Assert.IsNotNull(result);
            Assert.IsNull(result["error"]);
            Assert.AreEqual("TestScene", (string)result["sceneName"]);
            Assert.AreEqual(DefaultScenePath, (string)result["path"]);
            Assert.IsTrue((bool)result["isLoaded"]);

            // Verify scene was created
            Assert.IsTrue(File.Exists((string)result["path"]));

            // Clean up
            AssetDatabase.DeleteAsset((string)result["path"]);
        }

        [Test]
        public void CreateScene_ShouldWorkWithCustomPath()
        {
            var parameters = new JObject
            {
                ["sceneName"] = "CustomScene",
                ["path"] = testSceneFolder + "/"
            };

            var result = Create(parameters);

            Assert.IsNotNull(result);
            Assert.IsNull(result["error"]);
            Assert.AreEqual("CustomScene", (string)result["sceneName"]);
            Assert.AreEqual(testSceneFolder + "/CustomScene.unity", (string)result["path"]);

            // Verify scene was created
            Assert.IsTrue(File.Exists((string)result["path"]));
        }

        [Test]
        public void CreateScene_ShouldNotLoadScene_WhenLoadSceneIsFalse()
        {
            var currentScenePath = SceneManager.GetActiveScene().path;

            var parameters = new JObject
            {
                ["sceneName"] = "UnloadedScene",
                ["path"] = testSceneFolder + "/",
                ["loadScene"] = false
            };

            var result = Create(parameters);

            Assert.IsNotNull(result);
            Assert.IsNull(result["error"]);
            Assert.IsFalse((bool)result["isLoaded"]);

            // Verify current scene didn't change
            Assert.AreEqual(currentScenePath, SceneManager.GetActiveScene().path);
        }

        [Test]
        public void CreateScene_ShouldAddToBuildSettings_WhenRequested()
        {
            var parameters = new JObject
            {
                ["sceneName"] = "BuildScene",
                ["path"] = testSceneFolder + "/",
                ["addToBuildSettings"] = true
            };

            var result = Create(parameters);

            Assert.IsNotNull(result);
            Assert.IsNull(result["error"]);
            Assert.IsTrue((int)result["sceneIndex"] >= 0);

            // Verify scene is in build settings
            var buildScenes = EditorBuildSettings.scenes;
            Assert.IsTrue(buildScenes.Any(s => s.path == (string)result["path"]));
        }

        [Test]
        public void CreateScene_ShouldFailForEmptySceneName()
        {
            var parameters = new JObject
            {
                ["sceneName"] = ""
            };

            var result = Create(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("Scene name cannot be empty"));
        }

        [Test]
        public void CreateScene_ShouldFailForInvalidSceneName()
        {
            var parameters = new JObject
            {
                ["sceneName"] = "Invalid/Scene/Name"
            };

            var result = Create(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("invalid characters"));
        }

        [Test]
        public void CreateScene_ShouldFailForExistingScene()
        {
            // Create a scene first
            var scenePath = testSceneFolder + "/ExistingScene.unity";
            var newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(newScene, scenePath);

            var parameters = new JObject
            {
                ["sceneName"] = "ExistingScene",
                ["path"] = testSceneFolder + "/"
            };

            var result = Create(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("already exists"));
        }

        [Test]
        public void CreateScene_ShouldFailForInvalidPath()
        {
            var parameters = new JObject
            {
                ["sceneName"] = "TestScene",
                ["path"] = "../InvalidPath/"
            };

            var result = Create(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("Invalid path"));
        }

        [Test]
        public void CreateScene_ShouldHandleMissingParameters()
        {
            var parameters = new JObject();

            var result = Create(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("Scene name cannot be empty"));
        }
    }
}
