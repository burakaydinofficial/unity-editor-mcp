using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // manage_asset_import_settings per-platform texture overrides (E-tail slice 2). Creates a temp PNG + a temp
    // .txt under Assets/ in SetUp and deletes both in TearDown.
    public class TexturePlatformImportTests
    {
        private const string TexPath = "Assets/__import_platform_test__.png";
        private const string TxtPath = "Assets/__import_platform_test__.txt";

        [SetUp]
        public void SetUp()
        {
            var tex = new Texture2D(4, 4);
            var png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);
            File.WriteAllBytes(Path.Combine(Application.dataPath, "__import_platform_test__.png"), png);
            File.WriteAllText(Path.Combine(Application.dataPath, "__import_platform_test__.txt"), "not a texture");
            AssetDatabase.ImportAsset(TexPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(TxtPath, ImportAssetOptions.ForceSynchronousImport);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TexPath);
            AssetDatabase.DeleteAsset(TxtPath);
        }

        [Test]
        public void SetPlatform_Android_AppliesOverride()
        {
            var r = AssetImportSettingsHandler.HandleCommand("set_platform", new JObject
            {
                ["assetPath"] = TexPath,
                ["platform"] = "Android",
                ["maxTextureSize"] = 512,
                ["textureCompression"] = "Compressed"
            });
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);
            Assert.AreEqual("Android", (string)data["platform"]);
            Assert.IsTrue((bool)data["settings"]["overridden"]);
            Assert.AreEqual(512, (int)data["settings"]["maxTextureSize"]);
        }

        [Test]
        public void GetPlatform_AfterSet_ReflectsOverride()
        {
            AssetImportSettingsHandler.HandleCommand("set_platform", new JObject
            {
                ["assetPath"] = TexPath,
                ["platform"] = "Android",
                ["maxTextureSize"] = 256
            });
            var r = AssetImportSettingsHandler.HandleCommand("get_platform", new JObject
            {
                ["assetPath"] = TexPath,
                ["platform"] = "Android"
            });
            Assert.IsFalse(r.IsError, r.Error);
            var p = JObject.FromObject(r.Payload)["platform"];
            Assert.IsTrue((bool)p["overridden"]);
            Assert.AreEqual(256, (int)p["maxTextureSize"]);
        }

        [Test]
        public void GetPlatform_NoPlatform_ListsDefaultAndCommon()
        {
            var r = AssetImportSettingsHandler.HandleCommand("get_platform", new JObject { ["assetPath"] = TexPath });
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);
            Assert.IsNotNull(data["defaultPlatform"]);
            Assert.AreEqual(4, ((JArray)data["platforms"]).Count);
        }

        [Test]
        public void SetPlatform_InvalidFormat_ValidationError()
        {
            var r = AssetImportSettingsHandler.HandleCommand("set_platform", new JObject
            {
                ["assetPath"] = TexPath,
                ["platform"] = "Android",
                ["format"] = "NotARealFormat"
            });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("VALIDATION_ERROR", r.Code);
        }

        [Test]
        public void SetPlatform_NonTexture_InvalidState()
        {
            var r = AssetImportSettingsHandler.HandleCommand("set_platform", new JObject
            {
                ["assetPath"] = TxtPath,
                ["platform"] = "Android"
            });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("INVALID_STATE", r.Code);
        }

        [Test]
        public void SetPlatform_MissingPlatform_ValidationError()
        {
            var r = AssetImportSettingsHandler.HandleCommand("set_platform", new JObject { ["assetPath"] = TexPath });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("VALIDATION_ERROR", r.Code);
        }
    }
}
