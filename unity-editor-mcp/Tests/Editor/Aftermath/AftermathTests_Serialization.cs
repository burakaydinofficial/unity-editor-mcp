using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath tests for the Serialization category — the existing SerializedMemberHandlerTests cover
    /// inspect/set/array behavior against the IN-MEMORY ScriptableObject. This file fills the one high-value
    /// gap flagged for save_assets: prove PERSISTENCE. We dirty an asset through the real set_serialized_properties
    /// handler, call the save_assets handler, then re-read the bytes that landed on DISK via a wholly independent
    /// path (System.IO.File over the text-serialized .asset YAML) — never the in-memory object — and assert the
    /// written value persisted AND that an untouched field did not move on disk.
    ///
    /// Only save_assets is aftermath-tested here. inspect_serialized_object / set_serialized_properties /
    /// modify_serialized_array are already exhaustively covered (in-memory re-reads) in SerializedMemberHandlerTests
    /// and are NOT duplicated.
    /// </summary>
    [TestFixture]
    public class AftermathTests_Serialization
    {
        private const string AssetPath = "Assets/__aftermath_sertest__.asset";
        private SerFixtureAsset _asset;

        [SetUp]
        public void Setup()
        {
            _asset = ScriptableObject.CreateInstance<SerFixtureAsset>();
            // Default int/string fixture values are well known: IntField = 7, StringField = "hello".
            AssetDatabase.CreateAsset(_asset, AssetPath);
            AssetDatabase.SaveAssets(); // baseline on-disk state with the known defaults
        }

        [TearDown]
        public void Teardown()
        {
            AssetDatabase.DeleteAsset(AssetPath);
        }

        // Absolute on-disk path to the text-serialized .asset (Application.dataPath ends with "/Assets").
        private static string DiskPath()
        {
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), AssetPath);
        }

        [Test]
        public void SaveAssets_PersistsDirtiedPropertyToDisk_AndLeavesUntouchedFieldIntact()
        {
            // ---- baseline: capture the untouched field's on-disk presence BEFORE any write ----
            // StringField defaults to "hello" and we never touch it — it must survive the save unchanged.
            // A unique marker keeps the substring search on the YAML unambiguous.
            const int newInt = 314159;
            const string untouchedString = "hello";

            var diskBefore = File.ReadAllText(DiskPath());
            Assert.IsTrue(diskBefore.Contains("IntField: 7"), "fixture should serialize IntField: 7 by default");
            Assert.IsTrue(diskBefore.Contains(untouchedString), "fixture should serialize StringField 'hello' by default");
            Assert.IsFalse(diskBefore.Contains("IntField: " + newInt), "the new value must not be on disk yet");

            // ---- act 1: dirty the asset through the real set handler (CAS against the known current value) ----
            var setOutcome = SerializedMemberHandler.Set(new JObject
            {
                ["edits"] = new JArray
                {
                    new JObject
                    {
                        ["target"] = new JObject { ["assetPath"] = AssetPath },
                        ["set"] = new JObject
                        {
                            ["IntField"] = new JObject { ["value"] = newInt, ["expected"] = 7 }
                        }
                    }
                }
            });
            Assert.IsFalse(setOutcome.IsError, setOutcome.Error);
            Assert.AreEqual(1, ((JArray)JObject.FromObject(setOutcome.Payload)["changed"]).Count);

            // ---- act 2: explicit persist via the save_assets handler ----
            var saveOutcome = SerializedMemberHandler.SaveAssets(new JObject());
            Assert.IsFalse(saveOutcome.IsError, saveOutcome.Error);
            Assert.AreEqual("all-dirty", (string)JObject.FromObject(saveOutcome.Payload)["saved"]);

            // ---- aftermath: read the BYTES ON DISK independently of the in-memory object ----
            var diskAfter = File.ReadAllText(DiskPath());
            Assert.IsTrue(diskAfter.Contains("IntField: " + newInt),
                "save_assets must persist the dirtied IntField to the .asset on disk");
            Assert.IsFalse(diskAfter.Contains("IntField: 7"),
                "the stale default value must no longer be on disk");
            // untouched baseline did not move
            Assert.IsTrue(diskAfter.Contains(untouchedString),
                "the untouched StringField must remain unchanged on disk after the save");
        }

        [Test]
        public void SaveAssets_StringField_RoundTripsThroughFreshDiskLoad()
        {
            // Second, stronger independence check: after save, force-reimport the asset and load a FRESH instance
            // from disk (a different object than _asset), reading the value back through that fresh load.
            const string marker = "AFTERMATH_MARKER_VALUE_8842";

            var setOutcome = SerializedMemberHandler.Set(new JObject
            {
                ["edits"] = new JArray
                {
                    new JObject
                    {
                        ["target"] = new JObject { ["assetPath"] = AssetPath },
                        ["set"] = new JObject
                        {
                            ["StringField"] = new JObject { ["value"] = marker, ["expected"] = "hello" }
                        }
                    }
                }
            });
            Assert.IsFalse(setOutcome.IsError, setOutcome.Error);

            var saveOutcome = SerializedMemberHandler.SaveAssets(new JObject());
            Assert.IsFalse(saveOutcome.IsError, saveOutcome.Error);

            // Independent path 1: raw disk bytes.
            Assert.IsTrue(File.ReadAllText(DiskPath()).Contains(marker),
                "save_assets must write StringField to the .asset YAML on disk");

            // Independent path 2: force a reimport so AssetDatabase re-reads from disk, then load fresh.
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
            var fresh = AssetDatabase.LoadAssetAtPath<SerFixtureAsset>(AssetPath);
            Assert.IsNotNull(fresh, "fresh load of the saved asset should succeed");
            Assert.AreEqual(marker, fresh.StringField,
                "the persisted StringField must round-trip back from a fresh disk load");

            // Untouched baseline: IntField default survived the save.
            Assert.AreEqual(7, fresh.IntField, "the untouched IntField must remain at its default after the save");
        }
    }
}
