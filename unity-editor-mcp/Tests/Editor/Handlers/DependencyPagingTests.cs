using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // analyze_asset_dependencies paging (E-tail slice 4). SetUp creates a texture + a material that references
    // it (a real project-asset dependency); TearDown deletes both.
    public class DependencyPagingTests
    {
        private const string TexPath = "Assets/__dep_tex__.png";
        private const string MatPath = "Assets/__dep_mat__.mat";

        [SetUp]
        public void SetUp()
        {
            var tex = new Texture2D(4, 4);
            File.WriteAllBytes(Path.Combine(Application.dataPath, "__dep_tex__.png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(TexPath, ImportAssetOptions.ForceSynchronousImport);

            var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader);
            mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
            AssetDatabase.CreateAsset(mat, MatPath);
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(MatPath);
            AssetDatabase.DeleteAsset(TexPath);
        }

        private static JObject Deps(JObject p) =>
            JObject.FromObject(AssetDependencyHandler.HandleCommand("get_dependencies", p).Payload);

        [Test]
        public void GetDependencies_NonRecursive_FieldsAndDependency()
        {
            var data = Deps(new JObject { ["assetPath"] = MatPath });
            Assert.GreaterOrEqual((int)data["total"], 1);
            Assert.IsNotNull(data["hasMore"]);
            Assert.IsNotNull(data["offset"]);
            Assert.IsNotNull(data["limit"]);
            bool hasTex = ((JArray)data["dependencies"]).Any(d => ((string)d["path"]).EndsWith("__dep_tex__.png"));
            Assert.IsTrue(hasTex, "texture dependency should be listed");
        }

        [Test]
        public void GetDependencies_Recursive_Works()
        {
            var r = AssetDependencyHandler.HandleCommand("get_dependencies", new JObject { ["assetPath"] = MatPath, ["recursive"] = true });
            Assert.IsFalse(r.IsError, r.Error);
            Assert.GreaterOrEqual((int)JObject.FromObject(r.Payload)["total"], 1);
        }

        [Test]
        public void GetDependencies_LimitOne_CapsCount()
        {
            var data = Deps(new JObject { ["assetPath"] = MatPath, ["limit"] = 1 });
            Assert.LessOrEqual((int)data["count"], 1);
        }

        [Test]
        public void GetDependencies_OffsetBeyond_Empty()
        {
            var data = Deps(new JObject { ["assetPath"] = MatPath, ["offset"] = 999 });
            Assert.AreEqual(0, (int)data["count"]);
            Assert.IsFalse((bool)data["hasMore"]);
        }

        [Test]
        public void GetDependencies_MissingAsset_NotFound()
        {
            var r = AssetDependencyHandler.HandleCommand("get_dependencies", new JObject { ["assetPath"] = "Assets/__no_such_dep__.mat" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("NOT_FOUND", r.Code);
        }

        [Test]
        public void GetDependents_FindsMaterial()
        {
            var r = AssetDependencyHandler.HandleCommand("get_dependents", new JObject { ["assetPath"] = TexPath });
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);
            Assert.GreaterOrEqual((int)data["total"], 1);
            Assert.IsNotNull(data["hasMore"]);
        }
    }
}
