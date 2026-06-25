using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// AFTERMATH tests for the AssetQueries category — read-only sub-actions of the two multi-action
    /// asset tools (manage_asset_database: find_assets / get_asset_info; analyze_asset_dependencies:
    /// get_dependencies / get_dependents / analyze_size_impact / validate_references).
    ///
    /// Each test seeds a KNOWN graph (a material that references a real texture, plus a sibling that
    /// must not move), calls the HANDLER, then INDEPENDENTLY re-reads ground truth via a DIFFERENT
    /// API (raw AssetDatabase.GetDependencies / AssetPathToGUID / FileInfo) and asserts the handler's
    /// reported data matches that ground truth — not merely that it did not error. Every seeded asset
    /// is deleted in TearDown — zero residue.
    /// </summary>
    [TestFixture]
    public class AftermathTests_AssetQueries
    {
        // Dedicated sandbox folder so a crashed test never strands assets in real project folders.
        private const string Root = "Assets/__AftermathAssetQueries__";

        private string texPath;   // a real .png texture
        private string matPath;   // a material whose mainTexture references texPath (the real edge)
        private string siblingMatPath; // an unrelated material — the baseline that must not move

        [SetUp]
        public void Setup()
        {
            if (!AssetDatabase.IsValidFolder(Root))
            {
                AssetDatabase.CreateFolder("Assets", "__AftermathAssetQueries__");
            }

            texPath = Root + "/edge_tex.png";
            matPath = Root + "/edge_mat.mat";
            siblingMatPath = Root + "/sibling.mat";

            // Real, importable texture written to disk then imported (so it has a GUID + file bytes).
            var tex = new Texture2D(8, 8);
            File.WriteAllBytes(AbsPath(texPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceSynchronousImport);

            // Material that genuinely references the texture -> a real project-asset dependency edge.
            var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader);
            mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            AssetDatabase.CreateAsset(mat, matPath);

            // Unrelated sibling material (references nothing of ours) — the don't-touch baseline.
            var sibling = new Material(shader);
            AssetDatabase.CreateAsset(sibling, siblingMatPath);

            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Root))
            {
                AssetDatabase.DeleteAsset(Root);
            }
            AssetDatabase.Refresh();
        }

        private static string AbsPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        // =============================================================================================
        // manage_asset_database : find_assets -> AssetDatabaseHandler.HandleCommand("find_assets")
        // Aftermath: the returned asset list contains my known material at my known path with the GUID
        // that AssetDatabase.AssetPathToGUID reports — and only assets inside the searched folder.
        // =============================================================================================
        [Test]
        public void FindAssets_ByTypeFilterInFolder_ReturnsSeededMaterialWithGroundTruthGuid()
        {
            string expectedGuid = AssetDatabase.AssetPathToGUID(matPath);
            Assert.IsFalse(string.IsNullOrEmpty(expectedGuid), "Precondition: seeded material has no GUID");

            var r = AssetDatabaseHandler.HandleCommand("find_assets", new JObject
            {
                ["action"] = "find_assets",
                ["filter"] = "t:Material",
                ["searchInFolders"] = new JArray { Root }
            });
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);
            var assets = (JArray)data["assets"];
            Assert.IsNotNull(assets, "find_assets returned no assets array");

            // OUTCOME: my material is present, and the row the handler reports matches AssetDatabase truth.
            JObject mine = assets.Cast<JObject>().FirstOrDefault(a => (string)a["path"] == matPath);
            Assert.IsNotNull(mine, "Seeded material not found in find_assets results");
            Assert.AreEqual("edge_mat", (string)mine["name"], "Reported asset name mismatch");
            Assert.AreEqual("Material", (string)mine["type"], "Reported asset type mismatch");
            Assert.AreEqual(expectedGuid, (string)mine["guid"], "Reported GUID does not match AssetDatabase ground truth");

            // The unrelated sibling material is also a t:Material in this folder, so it should appear too
            // (confirms the filter really scoped to the folder rather than returning my material by luck).
            Assert.IsTrue(assets.Any(a => (string)a["path"] == siblingMatPath), "Sibling material missing from in-folder t:Material search");

            // Independent cross-check: every reported path actually lives under the searched folder.
            foreach (var a in assets)
            {
                Assert.IsTrue(((string)a["path"]).StartsWith(Root), "find_assets returned a path outside searchInFolders: " + a["path"]);
            }
        }

        // =============================================================================================
        // manage_asset_database : get_asset_info -> AssetDatabaseHandler.HandleCommand("get_asset_info")
        // Aftermath: reported path/type/guid match AssetDatabase, and the reported dependencies list
        // contains the texture edge that AssetDatabase.GetDependencies independently reports.
        // =============================================================================================
        [Test]
        public void GetAssetInfo_OnSeededMaterial_MatchesAssetDatabaseGroundTruth()
        {
            string expectedGuid = AssetDatabase.AssetPathToGUID(matPath);

            var r = AssetDatabaseHandler.HandleCommand("get_asset_info", new JObject
            {
                ["action"] = "get_asset_info",
                ["assetPath"] = matPath
            });
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);
            Assert.AreEqual(matPath, (string)data["assetPath"], "Echoed assetPath mismatch");

            var info = (JObject)data["info"];
            Assert.IsNotNull(info, "get_asset_info returned no info object");

            // OUTCOME: identity fields match AssetDatabase ground truth.
            Assert.AreEqual("edge_mat", (string)info["name"], "Reported name mismatch");
            Assert.AreEqual("Material", (string)info["type"], "Reported type mismatch");
            Assert.AreEqual(expectedGuid, (string)info["guid"], "Reported GUID does not match AssetDatabase.AssetPathToGUID");
            Assert.IsTrue((bool)info["isValid"], "Reported isValid should be true for a loadable asset");

            // OUTCOME: dependency edge matches the independent AssetDatabase reading. The handler excludes
            // self from dependencies, so compare against the same self-excluded ground-truth set.
            var groundTruthDeps = AssetDatabase.GetDependencies(matPath, false).Where(d => d != matPath).ToArray();
            Assert.Contains(texPath, groundTruthDeps, "Sanity: AssetDatabase itself does not see the texture edge");

            var reportedDeps = ((JArray)info["dependencies"]).Select(d => (string)d).ToArray();
            CollectionAssert.Contains(reportedDeps, texPath, "Reported dependencies omit the real texture edge");
            CollectionAssert.DoesNotContain(reportedDeps, matPath, "Reported dependencies should exclude self");
        }

        // =============================================================================================
        // analyze_asset_dependencies : get_dependencies -> HandleCommand("get_dependencies")
        // Aftermath: the forward edge material -> texture the handler reports is exactly the edge
        // AssetDatabase.GetDependencies reports; the unrelated sibling material is NOT among them.
        // =============================================================================================
        [Test]
        public void GetDependencies_OnMaterial_ReportsTextureEdgeMatchingAssetDatabase()
        {
            var r = AssetDependencyHandler.HandleCommand("get_dependencies", new JObject
            {
                ["assetPath"] = matPath
            });
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);
            var deps = ((JArray)data["dependencies"]).Cast<JObject>().ToList();

            // Independent ground truth.
            var truth = AssetDatabase.GetDependencies(matPath, false).Where(d => d != matPath).ToArray();
            CollectionAssert.Contains(truth, texPath, "Sanity: AssetDatabase does not report the texture edge");

            // OUTCOME: the handler reports the same forward edge, flagged as a direct dependency.
            JObject texEdge = deps.FirstOrDefault(d => (string)d["path"] == texPath);
            Assert.IsNotNull(texEdge, "get_dependencies omitted the real texture edge");
            Assert.IsTrue((bool)texEdge["isDirectDependency"], "Texture should be a direct dependency of the material");

            // The unrelated sibling material is not a dependency of mine.
            Assert.IsFalse(deps.Any(d => (string)d["path"] == siblingMatPath), "Unrelated sibling appeared as a dependency");
        }

        // =============================================================================================
        // analyze_asset_dependencies : get_dependents -> HandleCommand("get_dependents")
        // Aftermath: the reverse edge — the material is reported as a dependent of the texture, matching
        // an independent reverse scan; the unrelated sibling is NOT a dependent.
        // =============================================================================================
        [Test]
        public void GetDependents_OnTexture_ReportsMaterialAsDependent()
        {
            var r = AssetDependencyHandler.HandleCommand("get_dependents", new JObject
            {
                ["assetPath"] = texPath
            });
            Assert.IsFalse(r.IsError, r.Error);

            var data = JObject.FromObject(r.Payload);
            var dependents = ((JArray)data["dependents"]).Cast<JObject>().ToList();

            // Independent ground truth: does the material actually depend on the texture?
            bool matDependsOnTex = AssetDatabase.GetDependencies(matPath, false).Contains(texPath);
            Assert.IsTrue(matDependsOnTex, "Sanity: the material does not actually reference the texture");

            // OUTCOME: the handler's reverse scan reports the material as a dependent of the texture.
            Assert.IsTrue(dependents.Any(d => (string)d["path"] == matPath), "get_dependents omitted the referencing material");

            // The sibling material does NOT reference the texture, so it must not be reported.
            Assert.IsFalse(dependents.Any(d => (string)d["path"] == siblingMatPath), "Sibling wrongly reported as a dependent of the texture");
        }

        // =============================================================================================
        // analyze_asset_dependencies : analyze_size_impact -> HandleCommand("analyze_size_impact")
        // Aftermath: reported directSize matches the material's own file size on disk (KB), the recursive
        // dependency count includes the texture, and totalSize > directSize because the texture adds bytes.
        // =============================================================================================
        [Test]
        public void AnalyzeSizeImpact_OnMaterial_DirectSizeAndDependencyCountMatchDisk()
        {
            // Ground truth computed exactly like the handler (FileInfo length / 1024).
            int expectedDirectKB = (int)(new FileInfo(AbsPath(matPath)).Length / 1024);
            var truthDeps = AssetDatabase.GetDependencies(matPath, true).Where(d => d != matPath).ToArray();
            Assert.Contains(texPath, truthDeps, "Sanity: recursive deps do not include the texture");

            var r = AssetDependencyHandler.HandleCommand("analyze_size_impact", new JObject
            {
                ["assetPath"] = matPath
            });
            Assert.IsFalse(r.IsError, r.Error);

            var analysis = (JObject)JObject.FromObject(r.Payload)["analysis"];
            Assert.IsNotNull(analysis, "analyze_size_impact returned no analysis object");

            // OUTCOME: directSize is the material's own file size in KB (independent FileInfo read).
            Assert.AreEqual(expectedDirectKB, (int)analysis["directSize"], "Reported directSize does not match the material file size on disk");

            // OUTCOME: the recursive dependency count matches the self-excluded ground-truth set.
            Assert.AreEqual(truthDeps.Length, (int)analysis["dependencyCount"], "Reported dependencyCount does not match AssetDatabase recursive deps");
            Assert.GreaterOrEqual((int)analysis["dependencyCount"], 1, "Material must have at least the texture as a recursive dependency");

            // totalSize is direct + dependency bytes, so it is at least the direct size.
            Assert.GreaterOrEqual((int)analysis["totalSize"], expectedDirectKB, "totalSize should be >= directSize");
        }

        // =============================================================================================
        // analyze_asset_dependencies : validate_references -> HandleCommand("validate_references")
        // Aftermath: the project-wide scan succeeds and reports a coherent ledger
        // (validReferences + missingReferences accounting); and because my seeded material -> texture
        // edge is genuinely resolvable, neither my material nor my texture is ever flagged as a BROKEN
        // reference. (A negative assertion that holds regardless of the handler's 150-asset scan window:
        // a valid, loadable reference can never appear in brokenReferences.)
        // =============================================================================================
        [Test]
        public void ValidateReferences_NeverFlagsTheResolvableSeededEdgeAsBroken()
        {
            // Independent confirmation that the edge resolves (both ends load).
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(matPath), "Sanity: seeded material does not load");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(texPath), "Sanity: seeded texture does not load");

            var r = AssetDependencyHandler.HandleCommand("validate_references", new JObject());
            Assert.IsFalse(r.IsError, r.Error);

            var validation = (JObject)JObject.FromObject(r.Payload)["validation"];
            Assert.IsNotNull(validation, "validate_references returned no validation object");

            var broken = (JArray)validation["brokenReferences"];
            Assert.IsNotNull(broken, "validation has no brokenReferences array");

            // Ledger coherence: the reported missingReferences count equals the broken list length.
            Assert.AreEqual(broken.Count, (int)validation["missingReferences"], "missingReferences count disagrees with brokenReferences length");

            // OUTCOME: my resolvable texture edge is never reported as a missing reference.
            bool myEdgeFlagged = broken.Cast<JObject>().Any(b => (string)b["missingReference"] == texPath || (string)b["missingReference"] == matPath);
            Assert.IsFalse(myEdgeFlagged, "A resolvable seeded reference was wrongly reported as broken");
        }
    }
}
