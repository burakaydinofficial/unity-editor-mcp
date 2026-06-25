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
        // TextMeshPro entry's reported version EQUALS the REAL installed version. Round-2 fix: the prior re-read used
        // PackageInfo.FindForAssembly on the same "TMPro.TextMeshProUGUI, Unity.TextMeshPro" type string the handler
        // (ToolManagementHandler.PackageVersionForType) uses — same call, same source, so the equality was x==x and
        // proved nothing. The independent source of truth is now the host project's package files on disk
        // (Packages/packages-lock.json, falling back to Packages/manifest.json), parsed with Newtonsoft. Reading the
        // pinned "com.unity.textmeshpro" version off disk is a genuinely different derivation than the live
        // PackageManager assembly query the handler performs.
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

            bool reportedInstalled = (bool)tmpEntry["isInstalled"];
            string reportedVersion = (string)tmpEntry["version"];

            // Determine whether TMP is actually present (handler installed flag must agree with type resolution).
            var tmpType = System.Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");

            if (tmpType == null)
            {
                // TMP is absent from this host: the handler must report it not installed with the explicit sentinel.
                Assert.IsFalse(reportedInstalled,
                    "TMPro.TextMeshProUGUI did not resolve, so manage_tools must report TextMeshPro as not installed");
                Assert.AreEqual("Not installed", reportedVersion,
                    "an uninstalled built-in tool must report the 'Not installed' version sentinel");
                return;
            }

            // TMP is installed: the handler MUST report it installed.
            Assert.IsTrue(reportedInstalled,
                "TMPro.TextMeshProUGUI resolved, so manage_tools must report TextMeshPro as installed");

            // The reported version must never be a fabricated/sentinel placeholder, regardless of disk resolution.
            Assert.AreNotEqual("1.0.0", reportedVersion, "the fabricated '1.0.0' version must be gone");
            Assert.AreNotEqual("unknown", reportedVersion, "an installed tool must not fall back to the 'unknown' sentinel");
            Assert.AreNotEqual("Not installed", reportedVersion, "an installed tool must not report the 'Not installed' sentinel");

            // INDEPENDENT source of truth: the host project's package files on disk, NOT the live PackageManager
            // assembly query the handler uses. ProjectRoot is the parent of Application.dataPath (.../Assets).
            string projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName;
            string diskVersion = ReadTmpVersionFromPackageFiles(projectRoot);

            if (diskVersion != null)
            {
                Assert.AreEqual(diskVersion, reportedVersion,
                    "manage_tools must report TextMeshPro's REAL pinned package version from Packages/packages-lock.json " +
                    "(or manifest.json), not a value derived from the same PackageManager call the handler uses");
            }
            // else: TMP could not be resolved from the lock/manifest (e.g. it's an implicit transitive dependency on
            // this floor). Per the task, do NOT hard-fail — the sentinel-not-fabricated assertions above still stand.
        }

        // Reads the pinned "com.unity.textmeshpro" version straight off disk: prefer packages-lock.json (the resolved
        // graph, which records transitive deps too), then fall back to manifest.json (only direct deps). Returns null
        // when neither file pins TMP. Newtonsoft parse — a genuinely different path than UnityEditor.PackageManager.
        private static string ReadTmpVersionFromPackageFiles(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return null;

            // packages-lock.json: { "dependencies": { "com.unity.textmeshpro": { "version": "x.y.z" } } }
            string lockPath = System.IO.Path.Combine(projectRoot, "Packages", "packages-lock.json");
            if (System.IO.File.Exists(lockPath))
            {
                try
                {
                    var root = JObject.Parse(System.IO.File.ReadAllText(lockPath));
                    var entry = root["dependencies"]?["com.unity.textmeshpro"] as JObject;
                    var v = (string)entry?["version"];
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                catch { /* fall through to manifest */ }
            }

            // manifest.json: { "dependencies": { "com.unity.textmeshpro": "x.y.z" } }
            string manifestPath = System.IO.Path.Combine(projectRoot, "Packages", "manifest.json");
            if (System.IO.File.Exists(manifestPath))
            {
                try
                {
                    var root = JObject.Parse(System.IO.File.ReadAllText(manifestPath));
                    var v = (string)root["dependencies"]?["com.unity.textmeshpro"];
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                catch { /* return null below */ }
            }

            return null;
        }
    }
}
