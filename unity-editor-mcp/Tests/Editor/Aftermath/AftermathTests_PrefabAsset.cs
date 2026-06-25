using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using UnityEditorMCP.Handlers;
// COMPATIBILITY (see COMPATIBILITY.md): PrefabStageUtility moved namespaces in Unity 2021.2 —
// UnityEditor.Experimental.SceneManagement (<= 2021.1) -> UnityEditor.SceneManagement (2021.2+).
// This guarded alias keeps both the 2019.4 floor and newer editors compiling, mirroring the
// alias in AssetManagementHandler.cs.
#if UNITY_2021_2_OR_NEWER
using PrefabStageUtility = UnityEditor.SceneManagement.PrefabStageUtility;
#else
using PrefabStageUtility = UnityEditor.Experimental.SceneManagement.PrefabStageUtility;
#endif

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// AFTERMATH tests for the PrefabAsset category. Each test calls a tool HANDLER, then INDEPENDENTLY
    /// re-reads Unity state via a DIFFERENT path (raw AssetDatabase / PrefabUtility / PrefabStageUtility /
    /// File.Exists / GetComponent) and asserts the REAL effect happened AND that an unrelated baseline did
    /// NOT move. Every artifact created is deleted/restored in TearDown — zero residue.
    /// </summary>
    [TestFixture]
    public class AftermathTests_PrefabAsset
    {
        // Dedicated sandbox folder so a crashed test never strands assets in real project folders.
        private const string Root = "Assets/__AftermathPrefabAsset__";

        private GameObject sceneObject;

        [SetUp]
        public void Setup()
        {
            if (!AssetDatabase.IsValidFolder(Root))
            {
                AssetDatabase.CreateFolder("Assets", "__AftermathPrefabAsset__");
            }
        }

        [TearDown]
        public void TearDown()
        {
            // Make sure no prefab stage is left open between tests (would shadow scene lookups).
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                UnityEditor.SceneManagement.StageUtility.GoBackToPreviousStage();
            }

            if (sceneObject != null)
            {
                Object.DestroyImmediate(sceneObject);
                sceneObject = null;
            }

            if (AssetDatabase.IsValidFolder(Root))
            {
                AssetDatabase.DeleteAsset(Root);
            }
            AssetDatabase.Refresh();
        }

        // ---------------------------------------------------------------------------------------------
        // create_material -> AssetManagementHandler.CreateMaterial
        // Aftermath: the .mat loads off disk via AssetDatabase with the requested shader; an unrelated
        // sibling material's shader did NOT change.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void CreateMaterial_PersistsMaterialAssetWithShader_AndLeavesSiblingUntouched()
        {
            // Baseline: an unrelated material that must not be touched.
            string siblingPath = Root + "/Sibling.mat";
            var sibling = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(sibling, siblingPath);
            AssetDatabase.SaveAssets();
            string siblingShaderBefore = AssetDatabase.LoadAssetAtPath<Material>(siblingPath).shader.name;

            string matPath = Root + "/Created.mat";
            var parameters = new JObject
            {
                ["materialPath"] = matPath,
                ["shader"] = "Unlit/Color"
            };

            var r = AssetManagementHandler.CreateMaterial(parameters);
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH: re-read the material directly from the AssetDatabase (a different path than the
            // handler's return payload).
            var loaded = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            Assert.IsNotNull(loaded, "Material asset was not created on disk");
            Assert.AreEqual("Unlit/Color", loaded.shader.name, "Material has the wrong shader");

            // Baseline did not move.
            var siblingAfter = AssetDatabase.LoadAssetAtPath<Material>(siblingPath);
            Assert.IsNotNull(siblingAfter);
            Assert.AreEqual(siblingShaderBefore, siblingAfter.shader.name, "Unrelated sibling material's shader changed");
        }

        // ---------------------------------------------------------------------------------------------
        // create_prefab -> AssetManagementHandler.CreatePrefab (from a scene object)
        // Aftermath: the .prefab asset loads off disk AND carries the source's BoxCollider; the source
        // scene object still exists (SaveAsPrefabAssetAndConnect keeps it).
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void CreatePrefab_FromSceneObject_PersistsAssetWithSourceComponent()
        {
            sceneObject = new GameObject("AftermathSource");
            sceneObject.AddComponent<BoxCollider>();

            string prefabPath = Root + "/FromScene.prefab";
            var parameters = new JObject
            {
                ["gameObjectPath"] = "/AftermathSource",
                ["prefabPath"] = prefabPath
            };

            var r = AssetManagementHandler.CreatePrefab(parameters);
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH: load the prefab asset directly and inspect its contents.
            var loadedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(loadedPrefab, "Prefab asset was not created on disk");
            Assert.IsNotNull(loadedPrefab.GetComponent<BoxCollider>(), "Prefab is missing the source BoxCollider");

            // Baseline: the source scene object is still present (connect-mode keeps it in the scene).
            Assert.IsNotNull(GameObject.Find("/AftermathSource"), "Source scene object disappeared");
        }

        // ---------------------------------------------------------------------------------------------
        // modify_prefab -> AssetManagementHandler.ModifyPrefab (a SUCCESSFUL edit)
        // Aftermath: re-load the prefab asset and confirm the layer changed on disk; an unrelated prefab's
        // layer did NOT change.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void ModifyPrefab_EditPersistsOnAsset_AndLeavesOtherPrefabUntouched()
        {
            // Target prefab (created from a scene object so it is a real, loadable .prefab).
            sceneObject = new GameObject("AftermathModifyTarget");
            string targetPath = Root + "/ModifyTarget.prefab";
            PrefabUtility.SaveAsPrefabAsset(sceneObject, targetPath);

            // Baseline: an unrelated prefab whose layer must stay at 0.
            string otherPath = Root + "/Untouched.prefab";
            var otherGo = new GameObject("AftermathUntouched");
            PrefabUtility.SaveAsPrefabAsset(otherGo, otherPath);
            Object.DestroyImmediate(otherGo);
            int otherLayerBefore = AssetDatabase.LoadAssetAtPath<GameObject>(otherPath).layer;

            var parameters = new JObject
            {
                ["prefabPath"] = targetPath,
                ["applyToInstances"] = false,
                ["modifications"] = new JObject
                {
                    ["layer"] = 5 // a valid built-in layer index
                }
            };

            var r = AssetManagementHandler.ModifyPrefab(parameters);
            Assert.IsFalse(r.IsError, r.Error);
            var payload = JObject.FromObject(r.Payload);
            var modified = payload["modifiedProperties"].ToObject<string[]>();
            CollectionAssert.Contains(modified, "layer");

            // AFTERMATH: reload the asset fresh and assert the change is ON DISK.
            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(targetPath);
            Assert.IsNotNull(reloaded);
            Assert.AreEqual(5, reloaded.layer, "Layer change did not persist on the prefab asset");

            // Baseline: the other prefab is untouched.
            var otherAfter = AssetDatabase.LoadAssetAtPath<GameObject>(otherPath);
            Assert.AreEqual(otherLayerBefore, otherAfter.layer, "Unrelated prefab's layer changed");
        }

        // ---------------------------------------------------------------------------------------------
        // save_prefab -> AssetManagementHandler.SavePrefab (open stage, edit via modify_component, save)
        // Aftermath: after editing the stage root's BoxCollider and saving, reload the .prefab asset off
        // disk in a fresh load and confirm the edited value persisted.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void SavePrefab_InStage_PersistsComponentEditToDisk()
        {
            // Build a prefab with a BoxCollider whose isTrigger starts false.
            sceneObject = new GameObject("AftermathStageSource");
            var srcCollider = sceneObject.AddComponent<BoxCollider>();
            srcCollider.isTrigger = false;
            string prefabPath = Root + "/StageEdit.prefab";
            PrefabUtility.SaveAsPrefabAsset(sceneObject, prefabPath);
            Object.DestroyImmediate(sceneObject);
            sceneObject = null;

            // Sanity baseline: on disk isTrigger is false before any stage edit.
            Assert.IsFalse(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<BoxCollider>().isTrigger);

            // Open the prefab in stage mode via the handler.
            var openResult = AssetManagementHandler.OpenPrefab(new JObject { ["prefabPath"] = prefabPath });
            Assert.IsFalse(openResult.IsError, openResult.Error);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Assert.IsNotNull(stage, "Prefab stage did not open");

            // Address the stage root by its path (handler returns "/RootName" via GetGameObjectPath).
            string rootPath = "/" + stage.prefabContentsRoot.name;

            // Edit through modify_component (stage-aware resolution reaches the preview scene).
            var modifyResult = ComponentHandler.ModifyComponent(new JObject
            {
                ["gameObjectPath"] = rootPath,
                ["componentType"] = "BoxCollider",
                ["properties"] = new JObject { ["isTrigger"] = true }
            });
            Assert.IsFalse(modifyResult.IsError, modifyResult.Error);

            // Persist with save_prefab (in-stage form: no gameObjectPath).
            var saveResult = AssetManagementHandler.SavePrefab(new JObject());
            Assert.IsFalse(saveResult.IsError, saveResult.Error);

            // Exit the stage so the disk asset is the only source we read back.
            AssetManagementHandler.ExitPrefabMode(new JObject { ["saveChanges"] = false });
            Assert.IsNull(PrefabStageUtility.GetCurrentPrefabStage());

            // AFTERMATH: fully reload the asset from disk and confirm the edit persisted.
            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(reloaded);
            var reloadedCollider = reloaded.GetComponent<BoxCollider>();
            Assert.IsNotNull(reloadedCollider);
            Assert.IsTrue(reloadedCollider.isTrigger, "BoxCollider.isTrigger edit was not saved to the prefab asset on disk");
        }

        // ---------------------------------------------------------------------------------------------
        // exit_prefab_mode -> AssetManagementHandler.ExitPrefabMode
        // Aftermath: after exiting, PrefabStageUtility.GetCurrentPrefabStage() == null (guarded namespace),
        // and the on-disk prefab asset still exists.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void ExitPrefabMode_ClosesTheStage()
        {
            sceneObject = new GameObject("AftermathExitSource");
            string prefabPath = Root + "/ExitStage.prefab";
            PrefabUtility.SaveAsPrefabAsset(sceneObject, prefabPath);
            Object.DestroyImmediate(sceneObject);
            sceneObject = null;

            var openResult = AssetManagementHandler.OpenPrefab(new JObject { ["prefabPath"] = prefabPath });
            Assert.IsFalse(openResult.IsError, openResult.Error);
            Assert.IsNotNull(PrefabStageUtility.GetCurrentPrefabStage(), "Stage was not open before exit");

            var r = AssetManagementHandler.ExitPrefabMode(new JObject { ["saveChanges"] = true });
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH: the stage is closed (the core behavioral guarantee of the tool).
            Assert.IsNull(PrefabStageUtility.GetCurrentPrefabStage(), "Prefab stage was not closed after exit_prefab_mode");

            // Baseline: the prefab asset still exists on disk.
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), "Prefab asset vanished on exit");
        }

        // ---------------------------------------------------------------------------------------------
        // manage_asset_database create_folder -> AssetDatabaseHandler.HandleCommand("create_folder")
        // Aftermath: AssetDatabase.IsValidFolder reports the new folder exists; the parent sandbox folder
        // still exists.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void ManageAssetDatabase_CreateFolder_CreatesFolderOnDisk()
        {
            string newFolder = Root + "/NewFolder";
            Assert.IsFalse(AssetDatabase.IsValidFolder(newFolder), "Precondition: folder must not exist yet");

            var r = AssetDatabaseHandler.HandleCommand("create_folder", new JObject
            {
                ["action"] = "create_folder",
                ["folderPath"] = newFolder
            });
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH: independent existence check via AssetDatabase + the filesystem.
            Assert.IsTrue(AssetDatabase.IsValidFolder(newFolder), "Folder was not created in the AssetDatabase");
            string fsPath = Path.Combine(Application.dataPath, "..", newFolder);
            Assert.IsTrue(Directory.Exists(fsPath), "Folder does not exist on the filesystem");

            // Baseline: the parent sandbox folder is intact.
            Assert.IsTrue(AssetDatabase.IsValidFolder(Root), "Parent folder was disturbed");
        }

        // ---------------------------------------------------------------------------------------------
        // manage_asset_database move_asset -> AssetDatabaseHandler.HandleCommand("move_asset")
        // Aftermath: the asset is gone from the old path and loadable at the new path.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void ManageAssetDatabase_MoveAsset_RelocatesAsset()
        {
            string fromPath = Root + "/MoveMe.mat";
            string toPath = Root + "/Moved.mat";
            var mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, fromPath);
            AssetDatabase.SaveAssets();
            string guidBefore = AssetDatabase.AssetPathToGUID(fromPath);

            var r = AssetDatabaseHandler.HandleCommand("move_asset", new JObject
            {
                ["action"] = "move_asset",
                ["fromPath"] = fromPath,
                ["toPath"] = toPath
            });
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH: independent re-read via AssetDatabase.
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(fromPath), "Asset still present at the old path");
            var moved = AssetDatabase.LoadAssetAtPath<Material>(toPath);
            Assert.IsNotNull(moved, "Asset not found at the new path");
            // A move preserves the GUID (different path, same asset identity).
            Assert.AreEqual(guidBefore, AssetDatabase.AssetPathToGUID(toPath), "Move did not preserve the asset GUID");
        }

        // ---------------------------------------------------------------------------------------------
        // manage_asset_database copy_asset -> AssetDatabaseHandler.HandleCommand("copy_asset")
        // Aftermath: BOTH the source and a NEW copy (distinct GUID) exist after the copy.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void ManageAssetDatabase_CopyAsset_DuplicatesAsset()
        {
            string fromPath = Root + "/CopySrc.mat";
            string toPath = Root + "/CopyDst.mat";
            var mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, fromPath);
            AssetDatabase.SaveAssets();
            string srcGuidBefore = AssetDatabase.AssetPathToGUID(fromPath);

            var r = AssetDatabaseHandler.HandleCommand("copy_asset", new JObject
            {
                ["action"] = "copy_asset",
                ["fromPath"] = fromPath,
                ["toPath"] = toPath
            });
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH: source unchanged, copy exists, GUIDs are distinct (a real duplicate, not a move).
            var srcAfter = AssetDatabase.LoadAssetAtPath<Material>(fromPath);
            Assert.IsNotNull(srcAfter, "Source asset disappeared after copy");
            Assert.AreEqual(srcGuidBefore, AssetDatabase.AssetPathToGUID(fromPath), "Source GUID changed during copy");
            var copy = AssetDatabase.LoadAssetAtPath<Material>(toPath);
            Assert.IsNotNull(copy, "Copy was not created at the destination");
            Assert.AreNotEqual(srcGuidBefore, AssetDatabase.AssetPathToGUID(toPath), "Copy shares the source GUID (was a move, not a copy)");
        }
    }
}
