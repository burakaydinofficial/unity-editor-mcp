using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Editor-side path-traversal guards added in v0.5.0 (the audit found create_script -> arbitrary
    /// file WRITE and analyze_screenshot -> arbitrary file READ via `Assets/../..`). Defense-in-depth:
    /// the Node layer rejects these at the input edge, but a DIRECT TCP caller bypasses Node, so the C#
    /// handlers must reject them too — which is exactly what these tests pin.
    /// </summary>
    public class HandlerSecurityTests
    {
        [Test]
        public void CreateScript_WithDotDotTraversal_IsRejected()
        {
            var outcome = ScriptHandler.CreateScript(new JObject
            {
                ["scriptName"] = "TraversalProbe",
                ["path"] = "Assets/../../__traversal_probe"
            });
            Assert.IsTrue(outcome.IsError, "a traversal path must be rejected");
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
            StringAssert.Contains("traversal", outcome.Error);
        }

        [Test]
        public void CreateScript_WithBackslashTraversal_IsRejected()
        {
            var outcome = ScriptHandler.CreateScript(new JObject
            {
                ["scriptName"] = "TraversalProbe",
                ["path"] = "Assets/..\\..\\probe"
            });
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
        }

        [Test]
        public void AnalyzeScreenshot_WithTraversalImagePath_IsRejected()
        {
            var outcome = ScreenshotHandler.AnalyzeScreenshot(new JObject
            {
                ["imagePath"] = "Assets/../../secret.png",
                ["analysisType"] = "basic"
            });
            Assert.IsTrue(outcome.IsError, "a traversal imagePath must be rejected");
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
            StringAssert.Contains("project root", outcome.Error);
        }

        // The sibling script handlers + capture_screenshot were unguarded until the confirmation audit;
        // these are reachable with NO Node-side handler (pure passthrough via call_unity_tool), so the
        // C# guard is the sole defense.

        [Test]
        public void ReadScript_WithTraversalPath_IsRejected()
        {
            var outcome = ScriptHandler.ReadScript(new JObject { ["scriptPath"] = "Assets/../../secret.txt" });
            Assert.IsTrue(outcome.IsError, "a traversal scriptPath must be rejected (arbitrary file READ)");
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
            StringAssert.Contains("project root", outcome.Error);
        }

        [Test]
        public void UpdateScript_WithTraversalPath_IsRejected()
        {
            var outcome = ScriptHandler.UpdateScript(new JObject { ["scriptPath"] = "Assets/../../evil.cs", ["scriptContent"] = "x" });
            Assert.IsTrue(outcome.IsError, "a traversal scriptPath must be rejected (arbitrary file WRITE)");
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
            StringAssert.Contains("project root", outcome.Error);
        }

        [Test]
        public void CaptureScreenshot_WithTraversalOutputPath_IsRejected()
        {
            var outcome = ScreenshotHandler.CaptureScreenshot(new JObject { ["outputPath"] = "../../evil.png" });
            Assert.IsTrue(outcome.IsError, "a traversal outputPath must be rejected (write outside project)");
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
            StringAssert.Contains("project root", outcome.Error);
        }
    }
}
