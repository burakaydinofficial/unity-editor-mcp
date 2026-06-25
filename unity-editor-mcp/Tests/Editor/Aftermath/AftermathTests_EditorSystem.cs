using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath tests for the EditorSystem tool surface. Each test calls a tool HANDLER, then
    /// INDEPENDENTLY re-reads Unity state through a DIFFERENT path (raw PlayerSettings / Application /
    /// EditorApplication / Selection / the packages-lock-derived resolved set / the Core AuditLog) and asserts the REAL
    /// effect happened AND that unrelated state did NOT move. Every test restores any state it touches
    /// and leaves zero residue.
    ///
    /// SKIPPED tools (with reasons):
    ///  - quit_editor       : QuitEditor schedules EditorApplication.Exit(0) — it terminates the editor,
    ///                        so it cannot be exercised inside an EditMode batch run.
    ///  - manage_packages   : ManagePackages calls PackageManager.Client.Add/Remove, which resolves
    ///                        asynchronously and triggers a domain reload/recompile — unsafe + not
    ///                        synchronously verifiable in EditMode.
    /// </summary>
    [TestFixture]
    public class AftermathTests_EditorSystem
    {
        // get_editor_info -> the reported environment must match the live UnityEngine/EditorApplication state.
        [Test]
        public void GetEditorInfo_ReportsLiveEnvironment()
        {
            var r = EditorInfoHandler.GetEditorInfo(new JObject());
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);

            // Aftermath: re-read the same facts through the raw Unity APIs (a different path).
            Assert.AreEqual(Application.unityVersion, (string)data["unityVersion"]);
            Assert.AreEqual(Application.platform.ToString(), (string)data["platform"]);
            Assert.AreEqual(Application.productName, (string)data["productName"]);
            Assert.AreEqual(EditorApplication.isPlaying, (bool)data["isPlaying"]);
            Assert.IsFalse((bool)data["isPlaying"], "EditMode batch must not be in play mode");
            Assert.AreEqual(EditorUserBuildSettings.activeBuildTarget.ToString(), (string)data["activeBuildTarget"]);
        }

        // get_project_settings -> the reported settings must match live PlayerSettings; reading must not mutate.
        [Test]
        public void GetProjectSettings_MatchesLivePlayerSettings_AndDoesNotMutate()
        {
            // Baseline of settings that a READ must never move.
            string baselineProduct = PlayerSettings.productName;
            string baselineCompany = PlayerSettings.companyName;
            string baselineVersion = PlayerSettings.bundleVersion;

            var r = EditorInfoHandler.GetProjectSettings(new JObject());
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);

            // Aftermath: re-read via PlayerSettings directly.
            Assert.AreEqual(PlayerSettings.productName, (string)data["productName"]);
            Assert.AreEqual(PlayerSettings.companyName, (string)data["companyName"]);
            Assert.AreEqual(PlayerSettings.bundleVersion, (string)data["bundleVersion"]);
            Assert.AreEqual(PlayerSettings.colorSpace.ToString(), (string)data["colorSpace"]);

            // A read must not have changed anything.
            Assert.AreEqual(baselineProduct, PlayerSettings.productName);
            Assert.AreEqual(baselineCompany, PlayerSettings.companyName);
            Assert.AreEqual(baselineVersion, PlayerSettings.bundleVersion);
        }

        // list_packages -> the RESOLVED set (derived from packages-lock.json, NOT the manifest that backs
        // `dependencies`) must include newtonsoft-json, this package's hard UPM dependency, and must be at
        // least as large as the set of direct dependencies.
        [Test]
        public void ListPackages_ReflectsManifestDependencies()
        {
            var r = EditorInfoHandler.ListPackages(new JObject());
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);

            int count = (int)data["count"];
            Assert.GreaterOrEqual(count, 1, "the resolved package set must be non-empty");

            var resolved = (JArray)data["resolved"];
            Assert.Greater(resolved.Count, 0, "the resolved package set must be non-empty");

            var reportedDeps = (JObject)data["dependencies"];
            Assert.IsNotNull(reportedDeps);
            Assert.Greater(reportedDeps.Count, 0, "the project must declare at least one direct dependency");

            // Aftermath: cross-check the RESOLVED set (which the handler derives from packages-lock.json,
            // a genuinely DIFFERENT source than the manifest.json that backs `dependencies`). The resolved
            // closure includes transitive + built-in modules, so it must be at least as large as the set of
            // direct dependencies — and it must surface this package's hard UPM dependency by name.
            Assert.GreaterOrEqual(resolved.Count, reportedDeps.Count,
                "the resolved closure must include at least every direct dependency");

            const string hardDep = "com.unity.nuget.newtonsoft-json";
            bool hardDepResolved = false;
            foreach (var pkg in resolved)
            {
                if ((string)pkg["name"] == hardDep) { hardDepResolved = true; break; }
            }
            Assert.IsTrue(hardDepResolved,
                $"'{hardDep}' is a declared UPM dependency of this package and must appear in the resolved set");
        }

        // set_project_setting -> mutates productName; aftermath via raw PlayerSettings; companyName must
        // NOT move; original productName restored in finally.
        [Test]
        public void SetProjectSetting_ProductName_MutatesOnlyThatSetting()
        {
            string originalProduct = PlayerSettings.productName;
            string baselineCompany = PlayerSettings.companyName; // must NOT change
            string temp = "AftermathTemp_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                var r = EditorInfoHandler.SetProjectSetting(new JObject
                {
                    ["key"] = "productName",
                    ["value"] = temp
                });
                Assert.IsFalse(r.IsError, r.Error);

                var data = JObject.FromObject(r.Payload);
                Assert.AreEqual("productName", (string)data["key"]);

                // Aftermath: re-read via PlayerSettings (a different path than the handler's own getter).
                Assert.AreEqual(temp, PlayerSettings.productName, "productName was not actually written");
                // Unrelated setting must be untouched.
                Assert.AreEqual(baselineCompany, PlayerSettings.companyName, "companyName must not change");
            }
            finally
            {
                PlayerSettings.productName = originalProduct;
                AssetDatabase.SaveAssets();
            }

            // Confirm the restore actually landed.
            Assert.AreEqual(originalProduct, PlayerSettings.productName);
        }

        // get_audit_log -> append a KNOWN entry via the Core AuditLog (independent of the handler), then
        // assert the handler surfaces exactly that entry when filtered by its unique type.
        [Test]
        public void GetAuditLog_SurfacesAKnownEntry()
        {
            string path = AuditLogBridge.Path;
            string uniqueType = "aftermath_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string uniqueTarget = "target_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Snapshot existing content so we can restore the file untouched afterward.
            string backup = File.Exists(path) ? File.ReadAllText(path) : null;

            try
            {
                // Arrange: write a known mutation entry directly through Core (a different path than the handler).
                AuditLog.Append(path, uniqueType, uniqueTarget, true);

                // Act: read it back through the handler, filtered to our unique type.
                var r = AuditLogHandler.GetAuditLog(new JObject { ["type"] = uniqueType });
                Assert.IsFalse(r.IsError, r.Error);

                var data = JObject.FromObject(r.Payload);
                var entries = (JArray)data["entries"];
                Assert.AreEqual(1, entries.Count, "exactly the one known entry should match the unique type");
                Assert.AreEqual(1, (int)data["count"]);

                var entry = (JObject)entries[0];
                Assert.AreEqual(uniqueType, (string)entry["type"]);
                Assert.AreEqual(uniqueTarget, (string)entry["target"]);
                Assert.AreEqual(true, (bool)entry["ok"]);
            }
            finally
            {
                // Restore the audit log to its exact pre-test content.
                if (backup != null) File.WriteAllText(path, backup);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        // clear_audit_log -> after seeding an entry, clearing must empty the log (verified independently
        // via the Core AuditLog reader and the raw file).
        [Test]
        public void ClearAuditLog_EmptiesTheLog()
        {
            string path = AuditLogBridge.Path;
            string backup = File.Exists(path) ? File.ReadAllText(path) : null;

            try
            {
                // Arrange: ensure there IS at least one entry to clear.
                AuditLog.Append(path, "aftermath_clear_probe", "t", true);
                var before = AuditLog.Read(path, 100, null, null);
                Assert.Greater(before.Count, 0, "precondition: log must be non-empty before clear");

                // Act
                var r = AuditLogHandler.ClearAuditLog(new JObject());
                Assert.IsFalse(r.IsError, r.Error);
                var data = JObject.FromObject(r.Payload);
                Assert.AreEqual(true, (bool)data["cleared"]);

                // Aftermath: independent re-read confirms the log is empty (file removed by Clear()).
                var after = AuditLog.Read(path, 100, null, null);
                Assert.AreEqual(0, after.Count, "audit log must be empty after clear");
                Assert.IsFalse(File.Exists(path), "Clear deletes the file");
            }
            finally
            {
                // Restore prior content if there was any (clear removed the file).
                if (backup != null) File.WriteAllText(path, backup);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        // manage_selection (set) -> selecting a real GameObject by path must move Selection.activeGameObject
        // to it; a sibling created-but-unselected object must NOT become active.
        [Test]
        public void ManageSelection_Set_SelectsTheTargetObject()
        {
            var selectMe = new GameObject("AftermathSelectTarget");
            var leaveAlone = new GameObject("AftermathOtherObject");
            var previousSelection = Selection.objects; // restore later

            try
            {
                var r = SelectionHandler.HandleCommand("set", new JObject
                {
                    ["objectPaths"] = new JArray { "/AftermathSelectTarget" }
                });
                Assert.IsFalse(r.IsError, r.Error);

                var data = JObject.FromObject(r.Payload);
                Assert.AreEqual(1, (int)data["count"]);

                // Aftermath: re-read Unity's Selection directly.
                Assert.AreEqual(selectMe, Selection.activeGameObject, "the target must be the active selection");
                CollectionAssert.Contains(Selection.objects, selectMe);
                CollectionAssert.DoesNotContain(Selection.objects, leaveAlone,
                    "the unrelated object must not have been selected");
            }
            finally
            {
                Selection.objects = previousSelection;
                UnityEngine.Object.DestroyImmediate(selectMe);
                UnityEngine.Object.DestroyImmediate(leaveAlone);
            }
        }

        // manage_selection (clear) -> clearing must empty Selection (verified via the raw Selection API).
        [Test]
        public void ManageSelection_Clear_EmptiesSelection()
        {
            var go = new GameObject("AftermathClearTarget");
            var previousSelection = Selection.objects;

            try
            {
                Selection.activeGameObject = go; // arrange a non-empty selection
                Assert.AreEqual(go, Selection.activeGameObject); // precondition

                var r = SelectionHandler.HandleCommand("clear", new JObject());
                Assert.IsFalse(r.IsError, r.Error);

                // Aftermath: the raw Selection must now be empty.
                Assert.IsNull(Selection.activeGameObject, "selection must be cleared");
                Assert.AreEqual(0, Selection.objects.Length);
            }
            finally
            {
                Selection.objects = previousSelection;
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
