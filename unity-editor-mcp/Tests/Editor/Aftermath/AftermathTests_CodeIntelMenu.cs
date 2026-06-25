using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// AFTERMATH tests for the CodeIntelMenu category. Each test calls a tool HANDLER, then
    /// INDEPENDENTLY re-reads Unity state via a DIFFERENT API than the handler used and asserts
    /// (a) the real effect happened and (b) unrelated/baseline state did NOT move. All residue is
    /// removed in the test/teardown.
    ///
    /// Covered handlers:
    ///   - execute_menu_item (MenuHandler.ExecuteMenuItem) — built-in menus with deterministic,
    ///     scene-readable effects, plus the blacklist safety guard (negative aftermath: nothing changed).
    ///   - invoke_static_method (StaticInvokeHandler.InvokeStaticMethod) — a SIDE-EFFECTING editor
    ///     static method (EditorPrefs.SetInt) whose effect is re-read via a different API (GetInt).
    ///
    /// The syntactic code-intelligence handlers (get_symbols / find_symbol / find_references /
    /// get_symbol_body) and the reflection-based semantic ones (resolve_symbol / get_type_members /
    /// find_implementations) are NOT aftermath-tested here — see the SKIP notes at the bottom of this
    /// file for why (no Assets/ user-source fixture, and they are read-only with no state to re-read).
    /// </summary>
    [TestFixture]
    public class AftermathTests_CodeIntelMenu
    {
        // ------------------------------------------------------------------
        // execute_menu_item — built-in "GameObject/Create Empty"
        // Aftermath: a brand-new root GameObject appears in the ACTIVE scene, re-read via
        // SceneManager.GetActiveScene().GetRootGameObjects() (a different path than the handler),
        // and every pre-existing root is still present (baseline did not move).
        // ------------------------------------------------------------------
        [Test]
        public void ExecuteMenuItem_CreateEmpty_AddsRootGameObjectToActiveScene()
        {
            var scene = SceneManager.GetActiveScene();
            var baseline = scene.GetRootGameObjects();
            int baselineCount = baseline.Length;

            var r = MenuHandler.ExecuteMenuItem(new JObject
            {
                ["menuPath"] = "GameObject/Create Empty"
            });

            Assert.IsFalse(r.IsError, r.Error);
            var payload = JObject.FromObject(r.Payload);
            Assert.IsTrue((bool)payload["executed"], "the menu item must report it actually executed");

            // AFTERMATH (independent re-read): exactly one new root appeared, and the baseline roots
            // are all still present and untouched.
            var after = SceneManager.GetActiveScene().GetRootGameObjects();
            Assert.AreEqual(baselineCount + 1, after.Length,
                "Create Empty must add exactly one new root GameObject to the active scene");
            foreach (var b in baseline)
                Assert.Contains(b, after, "a pre-existing root GameObject must NOT be removed by the menu execution");

            var created = after.FirstOrDefault(go => !baseline.Contains(go));
            Assert.IsNotNull(created, "a new root GameObject must exist after Create Empty");
            // An empty GameObject has exactly a Transform and nothing else.
            Assert.IsNotNull(created.GetComponent<Transform>());
            Assert.AreEqual(1, created.GetComponents<Component>().Length,
                "an empty GameObject carries only its Transform");

            // Cleanup: remove the residue so the scene returns to baseline.
            UnityEngine.Object.DestroyImmediate(created);
            Assert.AreEqual(baselineCount, SceneManager.GetActiveScene().GetRootGameObjects().Length,
                "teardown must restore the active scene to its baseline root count");
        }

        // ------------------------------------------------------------------
        // execute_menu_item — built-in "GameObject/3D Object/Cube"
        // Aftermath: the created GameObject carries the components a primitive Cube must have
        // (MeshFilter with the built-in Cube mesh + a BoxCollider), re-read via GetComponent.
        // ------------------------------------------------------------------
        [Test]
        public void ExecuteMenuItem_CreateCube_AddsPrimitiveWithMeshAndCollider()
        {
            var scene = SceneManager.GetActiveScene();
            var baseline = scene.GetRootGameObjects();

            var r = MenuHandler.ExecuteMenuItem(new JObject
            {
                ["menuPath"] = "GameObject/3D Object/Cube"
            });

            Assert.IsFalse(r.IsError, r.Error);
            Assert.IsTrue((bool)JObject.FromObject(r.Payload)["executed"], "the cube menu item must actually execute");

            // AFTERMATH (independent re-read): a new root exists and looks like a primitive Cube.
            var after = SceneManager.GetActiveScene().GetRootGameObjects();
            var created = after.FirstOrDefault(go => !baseline.Contains(go));
            Assert.IsNotNull(created, "Create Cube must add a new root GameObject");

            var meshFilter = created.GetComponent<MeshFilter>();
            Assert.IsNotNull(meshFilter, "a primitive Cube must have a MeshFilter");
            Assert.IsNotNull(meshFilter.sharedMesh, "the Cube's MeshFilter must reference a mesh");
            Assert.AreEqual("Cube", meshFilter.sharedMesh.name, "the built-in primitive mesh must be the Cube mesh");
            Assert.IsNotNull(created.GetComponent<BoxCollider>(), "a primitive Cube must have a BoxCollider");

            // Cleanup.
            UnityEngine.Object.DestroyImmediate(created);
            Assert.IsNull(SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(go => !baseline.Contains(go)),
                "teardown must remove the created cube");
        }

        // ------------------------------------------------------------------
        // execute_menu_item — blacklist safety guard (NEGATIVE aftermath)
        // The handler must refuse a blacklisted menu (here "File/Quit") with INVALID_STATE, and the
        // editor state must be UNCHANGED: no menu ran, so the active scene's root set is identical and
        // (obviously) the editor is still alive to run the assertions below.
        // ------------------------------------------------------------------
        [Test]
        public void ExecuteMenuItem_BlacklistedQuit_RefusedAndNothingChanged()
        {
            var scene = SceneManager.GetActiveScene();
            var baseline = scene.GetRootGameObjects();
            int baselineCount = baseline.Length;

            var r = MenuHandler.ExecuteMenuItem(new JObject
            {
                ["menuPath"] = "File/Quit"
            });

            Assert.IsTrue(r.IsError, "a blacklisted menu must be refused, not executed");
            Assert.AreEqual("INVALID_STATE", r.Code);

            // AFTERMATH (independent re-read): the guard executed NO menu — the active scene's root set
            // is byte-for-byte the same baseline (the very fact this line runs proves the editor did
            // not quit).
            var after = SceneManager.GetActiveScene().GetRootGameObjects();
            Assert.AreEqual(baselineCount, after.Length, "a refused menu must not have mutated the scene");
            foreach (var b in baseline)
                Assert.Contains(b, after, "no baseline root may be added or removed by a refused menu");
        }

        // ------------------------------------------------------------------
        // invoke_static_method — a SIDE-EFFECTING editor static method.
        // EditorPrefs.SetInt(string,int) writes a persisted editor preference. The handler is
        // default-deny, so we allow-list exactly this call via UNITY_MCP_INVOKE_ALLOW, invoke it,
        // then re-read the effect through a DIFFERENT API (EditorPrefs.GetInt) and confirm the value
        // was actually written. The key is deleted (and the env var restored) in the finally block,
        // leaving zero residue. EditorPrefs is fully synchronous, deterministic and floor-stable;
        // it triggers no recompile, scene change or asset write.
        // ------------------------------------------------------------------
        [Test]
        public void InvokeStaticMethod_EditorPrefsSetInt_ValueReadBackViaGetInt()
        {
            const string typeName = "UnityEditor.EditorPrefs";
            const string prefKey = "UnityEditorMCP.Aftermath.CodeIntelMenu.Probe";
            const int writeValue = 4242;

            // Defensive pre-clean + baseline: the key must not pre-exist, and an unrelated control key
            // must remain untouched by the invoke (baseline that should NOT move).
            EditorPrefs.DeleteKey(prefKey);
            const string controlKey = "UnityEditorMCP.Aftermath.CodeIntelMenu.Control";
            const int controlValue = 7;
            EditorPrefs.SetInt(controlKey, controlValue);
            Assert.IsFalse(EditorPrefs.HasKey(prefKey), "precondition: the probe key must not exist yet");

            var prevAllow = Environment.GetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW");
            try
            {
                Environment.SetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW", typeName + ".*");

                var r = StaticInvokeHandler.InvokeStaticMethod(new JObject
                {
                    ["typeName"] = typeName,
                    ["methodName"] = "SetInt",
                    ["args"] = new JArray(prefKey, writeValue)
                });

                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);
                Assert.IsTrue((bool)payload["isVoid"], "EditorPrefs.SetInt returns void");

                // AFTERMATH (independent re-read via a DIFFERENT API than the invoke): the value is now
                // persisted and readable, and the unrelated control key was not disturbed.
                Assert.IsTrue(EditorPrefs.HasKey(prefKey), "SetInt must actually persist the key");
                Assert.AreEqual(writeValue, EditorPrefs.GetInt(prefKey, int.MinValue),
                    "EditorPrefs.GetInt must read back the exact value written by the invoked SetInt");
                Assert.AreEqual(controlValue, EditorPrefs.GetInt(controlKey, int.MinValue),
                    "an unrelated EditorPrefs key must NOT be changed by the invoke");
            }
            finally
            {
                // Cleanup: remove both keys and restore the env var, leaving zero residue.
                EditorPrefs.DeleteKey(prefKey);
                EditorPrefs.DeleteKey(controlKey);
                Environment.SetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW", prevAllow);
            }

            // Final aftermath: the probe key is gone (teardown restored the baseline).
            Assert.IsFalse(EditorPrefs.HasKey(prefKey), "teardown must delete the probe key");
        }
    }
}
