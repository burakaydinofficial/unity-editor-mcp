using NUnit.Framework;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // manage_build_settings (E-tail). Validation/list/clear/error paths; the happy-path add needs a real .unity
    // (dogfooded on the live editor). Restores the original EditorBuildSettings.scenes in TearDown.
    public class BuildSettingsTests
    {
        private EditorBuildSettingsScene[] _original;

        [SetUp] public void SetUp() { _original = EditorBuildSettings.scenes; }
        [TearDown] public void TearDown() { EditorBuildSettings.scenes = _original; }

        [Test] public void List_ReturnsShape()
        {
            var data = JObject.FromObject(SceneHandler.ManageBuildSettings(new JObject { ["action"] = "list" }).Payload);
            Assert.IsNotNull(data["scenes"]);
            Assert.IsNotNull(data["count"]);
            Assert.IsNotNull(data["enabledCount"]);
        }

        [Test] public void Add_NonexistentScene_NotFound()
        {
            var r = SceneHandler.ManageBuildSettings(new JObject { ["action"] = "add", ["scenePath"] = "Assets/__no_such_scene__.unity" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("NOT_FOUND", r.Code);
        }

        [Test] public void Add_OutOfProjectPath_ValidationError()
        {
            var r = SceneHandler.ManageBuildSettings(new JObject { ["action"] = "add", ["scenePath"] = "../evil.unity" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("VALIDATION_ERROR", r.Code);
        }

        [Test] public void Remove_BogusPath_NotFound()
        {
            var r = SceneHandler.ManageBuildSettings(new JObject { ["action"] = "remove", ["scenePath"] = "Assets/__not_in_build__.unity" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("NOT_FOUND", r.Code);
        }

        [Test] public void Clear_WithoutConfirm_Refused()
        {
            // clear wipes the whole build list un-undoably — it is confirm-gated (H3, bug hunt Mut-12).
            var r = SceneHandler.ManageBuildSettings(new JObject { ["action"] = "clear" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("CONFIRMATION_REQUIRED", r.Code);
        }

        [Test] public void Clear_WithConfirm_EmptiesList()
        {
            var r = SceneHandler.ManageBuildSettings(new JObject { ["action"] = "clear", ["confirm"] = true });
            Assert.IsFalse(r.IsError, r.Error);
            Assert.AreEqual(0, (int)JObject.FromObject(r.Payload)["count"]);
            Assert.AreEqual(0, EditorBuildSettings.scenes.Length);
        }

        [Test] public void UnknownAction_ValidationError()
        {
            var r = SceneHandler.ManageBuildSettings(new JObject { ["action"] = "frobnicate" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("VALIDATION_ERROR", r.Code);
        }
    }
}
