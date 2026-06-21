using NUnit.Framework;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    public class AssetLifecycleTests
    {
        private readonly List<string> _cleanup = new List<string>();
        private readonly List<GameObject> _gos = new List<GameObject>();

        [TearDown] public void Teardown()
        {
            // Delete in reverse (LIFO): a variant must go before its base, else the orphaned variant logs an error.
            for (int i = _cleanup.Count - 1; i >= 0; i--) AssetDatabase.DeleteAsset(_cleanup[i]);
            _cleanup.Clear();
            foreach (var g in _gos) if (g != null) Object.DestroyImmediate(g);
            _gos.Clear();
        }

        // ---- create_scriptable_object (E1) ----

        [Test] public void CreateScriptableObject_CreatesAsset()
        {
            var path = "Assets/__so_create__.asset"; _cleanup.Add(path);
            var r = AssetManagementHandler.CreateScriptableObject(new JObject { ["typeName"] = "UnityEditorMCP.Tests.SerFixtureAsset", ["assetPath"] = path });
            Assert.IsFalse(r.IsError, r.Error);
            Assert.IsInstanceOf<SerFixtureAsset>(AssetDatabase.LoadAssetAtPath<ScriptableObject>(path));
        }

        [Test] public void CreateScriptableObject_NotAScriptableObject()
        {
            var r = AssetManagementHandler.CreateScriptableObject(new JObject { ["typeName"] = "System.String", ["assetPath"] = "Assets/__so_bad__.asset" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("NOT_A_SCRIPTABLE_OBJECT", r.Code);
        }

        [Test] public void CreateScriptableObject_TypeNotFound()
        {
            var r = AssetManagementHandler.CreateScriptableObject(new JObject { ["typeName"] = "No.Such.Type", ["assetPath"] = "Assets/__so_nf__.asset" });
            Assert.AreEqual("TYPE_NOT_FOUND", r.Code);
        }

        [Test] public void CreateScriptableObject_PathExists()
        {
            var path = "Assets/__so_exists__.asset"; _cleanup.Add(path);
            Assert.IsFalse(AssetManagementHandler.CreateScriptableObject(new JObject { ["typeName"] = "UnityEditorMCP.Tests.SerFixtureAsset", ["assetPath"] = path }).IsError);
            var r2 = AssetManagementHandler.CreateScriptableObject(new JObject { ["typeName"] = "UnityEditorMCP.Tests.SerFixtureAsset", ["assetPath"] = path });
            Assert.AreEqual("PATH_EXISTS", r2.Code);
        }

        [Test] public void CreateScriptableObject_AbstractType_NotInstantiable()
        {
            var r = AssetManagementHandler.CreateScriptableObject(new JObject { ["typeName"] = "UnityEditorMCP.Tests.SerAbstractSO", ["assetPath"] = "Assets/__so_abstract__.asset" });
            Assert.AreEqual("NOT_INSTANTIABLE", r.Code);
        }

        [Test] public void CreateScriptableObject_Overwrite_NoDependents_Succeeds()
        {
            var path = "Assets/__so_overwrite__.asset"; _cleanup.Add(path);
            Assert.IsFalse(AssetManagementHandler.CreateScriptableObject(new JObject { ["typeName"] = "UnityEditorMCP.Tests.SerFixtureAsset", ["assetPath"] = path }).IsError);
            var r2 = AssetManagementHandler.CreateScriptableObject(new JObject { ["typeName"] = "UnityEditorMCP.Tests.SerFixtureAsset", ["assetPath"] = path, ["overwrite"] = true });
            Assert.IsFalse(r2.IsError, r2.Error); // no dependents -> overwrite proceeds without confirm
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ScriptableObject>(path));
        }

        // ---- unpack_prefab (E2) ----

        [Test] public void UnpackPrefab_RemovesInstanceLink()
        {
            var src = new GameObject("PfxUnpack", typeof(BoxCollider));
            var prefabPath = "Assets/__pfx_unpack__.prefab"; _cleanup.Add(prefabPath);
            var prefab = PrefabUtility.SaveAsPrefabAsset(src, prefabPath);
            Object.DestroyImmediate(src);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab); _gos.Add(inst);
            var r = AssetManagementHandler.UnpackPrefab(new JObject { ["instanceId"] = inst.GetInstanceID(), ["mode"] = "complete" });
            Assert.IsFalse(r.IsError, r.Error);
            Assert.IsFalse(PrefabUtility.IsPartOfPrefabInstance(inst)); // link removed
        }

        [Test] public void UnpackPrefab_NotAPrefabInstance()
        {
            var go = new GameObject("PlainGO"); _gos.Add(go);
            var r = AssetManagementHandler.UnpackPrefab(new JObject { ["instanceId"] = go.GetInstanceID() });
            Assert.AreEqual("NOT_A_PREFAB_INSTANCE", r.Code);
        }

        // ---- create_prefab_variant (E2) ----

        [Test] public void CreatePrefabVariant_ChainsToBase()
        {
            var src = new GameObject("PfxBase", typeof(BoxCollider));
            var basePath = "Assets/__pfx_base__.prefab"; _cleanup.Add(basePath);
            PrefabUtility.SaveAsPrefabAsset(src, basePath);
            Object.DestroyImmediate(src);
            var variantPath = "Assets/__pfx_variant__.prefab"; _cleanup.Add(variantPath);
            var r = AssetManagementHandler.CreatePrefabVariant(new JObject { ["basePrefabPath"] = basePath, ["variantPath"] = variantPath });
            Assert.IsFalse(r.IsError, r.Error);
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
            Assert.IsNotNull(variant);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(variant);
            Assert.IsNotNull(source);
            Assert.AreEqual(basePath, AssetDatabase.GetAssetPath(source)); // variant chains to the base
        }

        [Test] public void CreatePrefabVariant_PathExists()
        {
            var src = new GameObject("PfxBase2", typeof(BoxCollider));
            var basePath = "Assets/__pfx_base2__.prefab"; _cleanup.Add(basePath);
            PrefabUtility.SaveAsPrefabAsset(src, basePath);
            Object.DestroyImmediate(src);
            var variantPath = "Assets/__pfx_variant2__.prefab"; _cleanup.Add(variantPath);
            Assert.IsFalse(AssetManagementHandler.CreatePrefabVariant(new JObject { ["basePrefabPath"] = basePath, ["variantPath"] = variantPath }).IsError);
            var r2 = AssetManagementHandler.CreatePrefabVariant(new JObject { ["basePrefabPath"] = basePath, ["variantPath"] = variantPath });
            Assert.AreEqual("PATH_EXISTS", r2.Code);
        }

        // ---- delete gate (E1 / H3) ----

        [Test] public void DeleteAsset_RequiresConfirm()
        {
            var path = "Assets/__del_gate__.asset"; _cleanup.Add(path);
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<SerFixtureAsset>(), path);
            var r = AssetDatabaseHandler.HandleCommand("delete_asset", new JObject { ["assetPath"] = path });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("CONFIRMATION_REQUIRED", r.Code); // refusal is an error, consistent with the central gate
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ScriptableObject>(path)); // NOT deleted without confirm
        }

        [Test] public void DeleteAsset_WithConfirm_Deletes()
        {
            var path = "Assets/__del_confirm__.asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<SerFixtureAsset>(), path);
            var r = AssetDatabaseHandler.HandleCommand("delete_asset", new JObject { ["assetPath"] = path, ["confirm"] = true });
            Assert.IsFalse(r.IsError, r.Error);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<ScriptableObject>(path)); // deleted
        }
    }
}
