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
    public class LoadSceneTests
    {
        private string testSceneFolder = "Assets/TestScenes";
        private string testScenePath;
        private Scene originalScene;

        // Handlers return anonymous objects (internal to the Editor assembly), so
        // assert against a JObject view rather than `dynamic`: a missing key reads
        // as null instead of throwing RuntimeBinderException, and it works across
        // the assembly boundary without relying on InternalsVisibleTo.
        private static JObject Load(JObject parameters)
            => JObject.FromObject(SceneHandler.LoadScene(parameters));

        [SetUp]
        public void Setup()
        {
            // Save current scene state
            originalScene = SceneManager.GetActiveScene();

            // Create test folder if it doesn't exist
            if (!AssetDatabase.IsValidFolder(testSceneFolder))
            {
                AssetDatabase.CreateFolder("Assets", "TestScenes");
            }

            // Create a test scene
            testScenePath = testSceneFolder + "/LoadTestScene.unity";
            var testScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(testScene, testScenePath);

            // Add test scene to build settings
            var buildScenes = EditorBuildSettings.scenes.ToList();
            if (!buildScenes.Any(s => s.path == testScenePath))
            {
                buildScenes.Add(new EditorBuildSettingsScene(testScenePath, true));
                EditorBuildSettings.scenes = buildScenes.ToArray();
            }
        }

        [TearDown]
        public void TearDown()
        {
            // Remove test scene from build settings
            var buildScenes = EditorBuildSettings.scenes.ToList();
            buildScenes.RemoveAll(s => s.path.Contains("LoadTestScene"));
            EditorBuildSettings.scenes = buildScenes.ToArray();

            // Clean up test scenes
            if (AssetDatabase.IsValidFolder(testSceneFolder))
            {
                AssetDatabase.DeleteAsset(testSceneFolder);
            }
        }

        [Test]
        public void LoadScene_ShouldLoadByPath()
        {
            var parameters = new JObject
            {
                ["scenePath"] = testScenePath
            };

            var result = Load(parameters);

            Assert.IsNotNull(result);
            Assert.IsNull(result["error"]);
            Assert.AreEqual("LoadTestScene", (string)result["sceneName"]);
            Assert.AreEqual(testScenePath, (string)result["scenePath"]);
            Assert.AreEqual("Single", (string)result["loadMode"]);
            Assert.IsTrue((bool)result["isLoaded"]);

            // Verify scene is actually loaded
            Assert.AreEqual("LoadTestScene", SceneManager.GetActiveScene().name);
        }

        [Test]
        public void LoadScene_ShouldLoadByName()
        {
            // Ensure scene is in build settings
            var parameters = new JObject
            {
                ["sceneName"] = "LoadTestScene"
            };

            var result = Load(parameters);

            Assert.IsNotNull(result);
            Assert.IsNull(result["error"]);
            Assert.AreEqual("LoadTestScene", (string)result["sceneName"]);
            Assert.IsTrue((bool)result["isLoaded"]);
        }

        [Test]
        public void LoadScene_ShouldLoadAdditively()
        {
            // First create another scene to have multiple scenes
            var additiveScenePath = testSceneFolder + "/AdditiveTestScene.unity";
            var additiveScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
            EditorSceneManager.SaveScene(additiveScene, additiveScenePath);

            var parameters = new JObject
            {
                ["scenePath"] = additiveScenePath,
                ["loadMode"] = "Additive"
            };

            var result = Load(parameters);

            Assert.IsNotNull(result);
            Assert.IsNull(result["error"]);
            Assert.AreEqual("AdditiveTestScene", (string)result["sceneName"]);
            Assert.AreEqual("Additive", (string)result["loadMode"]);
            Assert.IsTrue((bool)result["isLoaded"]);
            Assert.IsTrue((int)result["activeSceneCount"] > 1);

            // Verify multiple scenes are loaded
            Assert.AreEqual(2, SceneManager.sceneCount);
        }

        [Test]
        public void LoadScene_ShouldFailForMissingParameters()
        {
            var parameters = new JObject();

            var result = Load(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("Either scenePath or sceneName must be provided"));
        }

        [Test]
        public void LoadScene_ShouldFailForBothParameters()
        {
            var parameters = new JObject
            {
                ["scenePath"] = testScenePath,
                ["sceneName"] = "LoadTestScene"
            };

            var result = Load(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("Provide either scenePath or sceneName, not both"));
        }

        [Test]
        public void LoadScene_ShouldFailForInvalidLoadMode()
        {
            var parameters = new JObject
            {
                ["scenePath"] = testScenePath,
                ["loadMode"] = "InvalidMode"
            };

            var result = Load(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("Invalid load mode"));
        }

        [Test]
        public void LoadScene_ShouldFailForNonExistentScenePath()
        {
            var parameters = new JObject
            {
                ["scenePath"] = "Assets/NonExistent/Scene.unity"
            };

            var result = Load(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("Scene file not found"));
        }

        [Test]
        public void LoadScene_ShouldFailForSceneNotInBuildSettings()
        {
            // Create a scene not in build settings
            var notInBuildPath = testSceneFolder + "/NotInBuild.unity";
            var notInBuildScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(notInBuildScene, notInBuildPath);

            var parameters = new JObject
            {
                ["sceneName"] = "NotInBuild"
            };

            var result = Load(parameters);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result["error"]);
            Assert.IsTrue(((string)result["error"]).Contains("not in build settings"));
        }

        [Test]
        public void LoadScene_ShouldReturnPreviousSceneInfo()
        {
            // Load a known scene first
            EditorSceneManager.OpenScene(testScenePath);
            var previousSceneName = SceneManager.GetActiveScene().name;

            // Create another scene to load
            var newScenePath = testSceneFolder + "/NewTestScene.unity";
            var newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(newScene, newScenePath);

            // NewScene(Single) made the new scene active; re-open the original so it
            // is the active (and therefore "previous") scene when we load below.
            EditorSceneManager.OpenScene(testScenePath);
            previousSceneName = SceneManager.GetActiveScene().name;

            var parameters = new JObject
            {
                ["scenePath"] = newScenePath
            };

            var result = Load(parameters);

            Assert.IsNotNull(result);
            Assert.IsNull(result["error"]);
            Assert.AreEqual(previousSceneName, (string)result["previousScene"]);
        }
    }
}
