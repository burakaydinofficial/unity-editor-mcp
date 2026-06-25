using System;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath tests for the SystemRead tool surface (editor/compilation introspection +
    /// tool management). Each test calls a tool HANDLER, then INDEPENDENTLY re-reads the same
    /// fact through a DIFFERENT path (raw EditorApplication / Application / UnityEditor.PackageManager)
    /// and asserts the REAL value matches — never just "did not error". Read-only handlers must not
    /// mutate editor state, so each test also captures a baseline of what must NOT move and confirms
    /// it didn't. Zero residue: nothing is created and no settings are touched.
    ///
    /// SKIPPED tools (with reasons):
    ///  - handshake : the payload is produced by the PRIVATE BuildHandshakePayload() and served only
    ///                through the PRIVATE static CommandDispatcher (_dispatcher) in
    ///                Editor/Core/UnityEditorMCP.cs — neither is invokable as a handler from the test
    ///                assembly. Reconstructing the payload from CommandCatalog here would only test the
    ///                catalog against itself (the handler's actual derivation is unreachable), so this is
    ///                a hollow no-error test and is skipped rather than written.
    /// </summary>
    [TestFixture]
    public class AftermathTests_SystemRead
    {
        // get_editor_state -> the reported transient state must match the live EditorApplication, and the
        // reported applicationPath/contentsPath must match the raw EditorApplication strings. In an idle
        // EditMode batch run the editor is neither playing nor compiling; a state READ must not flip any of it.
        [Test]
        public void GetEditorState_ReportsLiveEditorApplicationState()
        {
            // Baseline of state that a pure READ must never move.
            bool baselinePlaying = EditorApplication.isPlaying;
            bool baselinePaused = EditorApplication.isPaused;

            var r = PlayModeHandler.HandleCommand("get_editor_state", new JObject());
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);
            var state = (JObject)data["state"];
            Assert.IsNotNull(state, "get_editor_state must return a 'state' object");

            // Aftermath: re-read each fact through the raw EditorApplication API (a different path than
            // the handler's own getter).
            Assert.AreEqual(EditorApplication.isPlaying, (bool)state["isPlaying"]);
            Assert.AreEqual(EditorApplication.isPaused, (bool)state["isPaused"]);
            Assert.AreEqual(EditorApplication.isCompiling, (bool)state["isCompiling"]);
            Assert.AreEqual(EditorApplication.isUpdating, (bool)state["isUpdating"]);
            Assert.AreEqual(EditorApplication.applicationPath, (string)state["applicationPath"]);
            Assert.AreEqual(EditorApplication.applicationContentsPath, (string)state["applicationContentsPath"]);

            // An idle EditMode batch run must be settled: not playing, not compiling.
            Assert.IsFalse((bool)state["isPlaying"], "EditMode batch must not be in play mode");
            Assert.IsFalse((bool)state["isCompiling"], "an idle EditMode run must not be compiling");

            // A read must not have changed editor state.
            Assert.AreEqual(baselinePlaying, EditorApplication.isPlaying, "reading state must not start play mode");
            Assert.AreEqual(baselinePaused, EditorApplication.isPaused, "reading state must not pause");
        }

        // get_compilation_state -> the reported isCompiling/isUpdating must match the live EditorApplication
        // flags, and at idle (the only safe state in this run) isCompiling must be false. The header counts
        // must be internally consistent with the returned messages array; a read must not move the live flags.
        [Test]
        public void GetCompilationState_MatchesLiveCompilationFlags()
        {
            // Baseline the live flags a READ must not move.
            bool baselineCompiling = EditorApplication.isCompiling;
            bool baselineUpdating = EditorApplication.isUpdating;

            var r = CompilationHandler.GetCompilationState(new JObject { ["includeMessages"] = true });
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);

            // Aftermath: re-read the flags via EditorApplication directly.
            Assert.AreEqual(EditorApplication.isCompiling, (bool)data["isCompiling"]);
            Assert.AreEqual(EditorApplication.isUpdating, (bool)data["isUpdating"]);

            // The test runner itself only runs once compilation is finished, so an idle EditMode run is settled.
            Assert.IsFalse((bool)data["isCompiling"], "an idle EditMode run must report isCompiling == false");

            // The header counts must be self-consistent with the returned messages.
            var messages = (JArray)data["messages"];
            Assert.IsNotNull(messages, "includeMessages:true must return a 'messages' array");
            Assert.AreEqual(messages.Count, (int)data["messageCount"],
                "messageCount must equal the returned messages array length");

            int errorCount = 0, warningCount = 0;
            foreach (var m in messages)
            {
                var type = (string)m["type"];
                if (type == "Error") errorCount++;
                else if (type == "Warning") warningCount++;
            }
            Assert.AreEqual(errorCount, (int)data["errorCount"], "errorCount must match the Error messages present");
            Assert.AreEqual(warningCount, (int)data["warningCount"], "warningCount must match the Warning messages present");

            // A read must not have moved the live compilation flags.
            Assert.AreEqual(baselineCompiling, EditorApplication.isCompiling);
            Assert.AreEqual(baselineUpdating, EditorApplication.isUpdating);
        }

        // get_compilation_state (includeMessages:false) -> must still report flags matching EditorApplication
        // and must OMIT the messages array, while the count headers remain present and the flags don't move.
        [Test]
        public void GetCompilationState_WithoutMessages_OmitsMessagesButKeepsFlags()
        {
            bool baselineCompiling = EditorApplication.isCompiling;

            var r = CompilationHandler.GetCompilationState(new JObject { ["includeMessages"] = false });
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);

            // Aftermath: flags still re-read true against the raw API.
            Assert.AreEqual(EditorApplication.isCompiling, (bool)data["isCompiling"]);
            Assert.AreEqual(EditorApplication.isUpdating, (bool)data["isUpdating"]);
            Assert.IsFalse((bool)data["isCompiling"], "an idle EditMode run must report isCompiling == false");

            // includeMessages:false must drop the heavy array but keep the summary headers.
            Assert.IsNull(data["messages"], "includeMessages:false must not return a 'messages' array");
            Assert.IsNotNull(data["messageCount"], "the messageCount header must still be present");
            Assert.IsNotNull(data["errorCount"], "the errorCount header must still be present");

            Assert.AreEqual(baselineCompiling, EditorApplication.isCompiling, "a read must not move the compile flag");
        }

        // manage_tools (get) -> UPGRADE over Round4RegressionTests.ManageTools_NoFabricatedPackages: assert the
        // TextMeshPro entry's reported version EQUALS the REAL installed version, re-read independently via
        // UnityEditor.PackageManager.PackageInfo.FindForAssembly on the live TMPro.TextMeshProUGUI assembly.
        // When TextMeshPro is not present in the host project, the handler must report it as not installed
        // (an equally independent outcome via the same type-resolution path).
        [Test]
        public void ManageTools_Get_TextMeshProVersionMatchesRealInstalledVersion()
        {
            var r = ToolManagementHandler.HandleCommand("get", new JObject());
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);
            var tools = (JArray)data["tools"];
            Assert.IsNotNull(tools, "manage_tools get must return a 'tools' array");

            // Locate the TextMeshPro entry the handler reported.
            JObject tmpEntry = null;
            foreach (var t in tools)
            {
                if ((string)t["name"] == "TextMeshPro") { tmpEntry = (JObject)t; break; }
            }
            Assert.IsNotNull(tmpEntry, "manage_tools must always advertise the built-in TextMeshPro tool entry");

            // Independent re-read: resolve the real TMP type and its real package version through Package Manager.
            var tmpType = System.Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
            bool reportedInstalled = (bool)tmpEntry["isInstalled"];

            if (tmpType != null)
            {
                // TMP is installed: the handler MUST report it installed, and its version MUST equal the
                // real PackageInfo.FindForAssembly version (the independent source of truth).
                Assert.IsTrue(reportedInstalled,
                    "TMPro.TextMeshProUGUI resolved, so manage_tools must report TextMeshPro as installed");

                var realInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(tmpType.Assembly);
                Assert.IsNotNull(realInfo,
                    "PackageInfo.FindForAssembly must resolve the package that owns TMPro.TextMeshProUGUI");
                string realVersion = realInfo.version;

                Assert.AreEqual(realVersion, (string)tmpEntry["version"],
                    "manage_tools must report TextMeshPro's REAL installed package version, not a hardcoded guess");
                // Sanity: it must not be the old fabricated "1.0.0" placeholder nor the "unknown"/"Not installed" sentinels.
                Assert.AreNotEqual("1.0.0", (string)tmpEntry["version"], "the fabricated '1.0.0' version must be gone");
                Assert.AreNotEqual("Not installed", (string)tmpEntry["version"]);
            }
            else
            {
                // TMP is absent from this host: the handler must report it not installed with the explicit sentinel.
                Assert.IsFalse(reportedInstalled,
                    "TMPro.TextMeshProUGUI did not resolve, so manage_tools must report TextMeshPro as not installed");
                Assert.AreEqual("Not installed", (string)tmpEntry["version"],
                    "an uninstalled built-in tool must report the 'Not installed' version sentinel");
            }
        }
    }
}
