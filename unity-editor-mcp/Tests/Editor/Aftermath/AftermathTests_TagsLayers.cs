using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath-asserting tests for the TagsLayers category (manage_tags ->
    /// TagManagementHandler, manage_layers -> LayerManagementHandler).
    ///
    /// Each test calls the handler, then INDEPENDENTLY re-reads ProjectSettings via a
    /// different Unity API than the handler's write path (the handlers write through a
    /// SerializedObject on TagManager.asset; these tests read back through
    /// UnityEditorInternal.InternalEditorUtility.tags and UnityEngine.LayerMask), asserts
    /// the real effect happened, and asserts that unrelated reserved tags/layers did NOT
    /// move. Every test restores the original tag/layer configuration in a finally block —
    /// these mutate ProjectSettings, so leaving residue would corrupt the host project.
    /// </summary>
    [TestFixture]
    public class AftermathTests_TagsLayers
    {
        // Names chosen to be extremely unlikely to pre-exist in any host project.
        private const string TempTagName = "__MCP_Aftermath_TempTag_98213__";
        private const string TempLayerName = "__MCP_AftermathLayer_771__";

        private static bool TagExists(string name)
        {
            return InternalEditorUtility.tags.Contains(name);
        }

        private static int FindLayerIndex(string name)
        {
            for (int i = 0; i < 32; i++)
            {
                if (LayerMask.LayerToName(i) == name)
                {
                    return i;
                }
            }
            return -1;
        }

        // ---------------------------------------------------------------------
        // manage_tags
        // ---------------------------------------------------------------------

        [Test]
        public void ManageTags_Add_RealTagAppears_ReservedUnchanged()
        {
            // Pre-clean any residue from a prior aborted run.
            if (TagExists(TempTagName))
            {
                TagManagementHandler.RemoveTag(TempTagName);
            }

            // Baseline: the reserved tags that must NOT change.
            bool untaggedBefore = TagExists("Untagged");
            bool playerBefore = TagExists("Player");
            bool mainCameraBefore = TagExists("MainCamera");
            Assert.IsFalse(TagExists(TempTagName), "Temp tag must not exist before the test.");

            try
            {
                // Act: add via the handler (writes through SerializedObject on TagManager.asset).
                var r = TagManagementHandler.HandleCommand("add",
                    new JObject { ["action"] = "add", ["tagName"] = TempTagName });

                // No error.
                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);
                Assert.AreEqual(TempTagName, (string)payload["tagName"]);

                // AFTERMATH: re-read via InternalEditorUtility.tags (a DIFFERENT path than the
                // SerializedObject write) and assert the real effect.
                Assert.IsTrue(TagExists(TempTagName),
                    "Tag should be present in InternalEditorUtility.tags after add.");

                // Unrelated reserved tags did NOT move.
                Assert.AreEqual(untaggedBefore, TagExists("Untagged"), "'Untagged' must be unchanged.");
                Assert.AreEqual(playerBefore, TagExists("Player"), "'Player' must be unchanged.");
                Assert.AreEqual(mainCameraBefore, TagExists("MainCamera"), "'MainCamera' must be unchanged.");
            }
            finally
            {
                // Restore: remove the temp tag if it is still present.
                if (TagExists(TempTagName))
                {
                    TagManagementHandler.RemoveTag(TempTagName);
                }
                Assert.IsFalse(TagExists(TempTagName), "Cleanup failed: temp tag still present.");
            }
        }

        [Test]
        public void ManageTags_Remove_RealTagDisappears_ReservedUnchanged()
        {
            // Arrange: ensure the temp tag exists first (via the add handler).
            if (!TagExists(TempTagName))
            {
                var addOutcome = TagManagementHandler.AddTag(TempTagName);
                Assert.IsFalse(addOutcome.IsError, addOutcome.Error);
            }
            Assert.IsTrue(TagExists(TempTagName), "Arrange failed: temp tag should exist before remove.");

            bool untaggedBefore = TagExists("Untagged");
            bool playerBefore = TagExists("Player");

            bool removed = false;
            try
            {
                // Act: remove via the handler.
                var r = TagManagementHandler.HandleCommand("remove",
                    new JObject { ["action"] = "remove", ["tagName"] = TempTagName });

                Assert.IsFalse(r.IsError, r.Error);
                removed = true;

                // AFTERMATH: independently confirm the tag is gone.
                Assert.IsFalse(TagExists(TempTagName),
                    "Tag should be absent from InternalEditorUtility.tags after remove.");

                // Unrelated reserved tags did NOT move.
                Assert.AreEqual(untaggedBefore, TagExists("Untagged"), "'Untagged' must be unchanged.");
                Assert.AreEqual(playerBefore, TagExists("Player"), "'Player' must be unchanged.");
            }
            finally
            {
                // Restore: if remove did not complete, clean up the residue.
                if (!removed && TagExists(TempTagName))
                {
                    TagManagementHandler.RemoveTag(TempTagName);
                }
                Assert.IsFalse(TagExists(TempTagName), "Cleanup failed: temp tag still present.");
            }
        }

        // ---------------------------------------------------------------------
        // manage_layers
        // ---------------------------------------------------------------------

        [Test]
        public void ManageLayers_Add_RealLayerAppears_ReservedUnchanged()
        {
            // Pre-clean residue.
            if (FindLayerIndex(TempLayerName) != -1)
            {
                LayerManagementHandler.RemoveLayer(TempLayerName);
            }

            // Baseline: reserved layers at their fixed indices must NOT change.
            string layer0Before = LayerMask.LayerToName(0); // "Default"
            string layer1Before = LayerMask.LayerToName(1); // "TransparentFX"
            string layer5Before = LayerMask.LayerToName(5); // "UI"
            Assert.AreEqual(-1, FindLayerIndex(TempLayerName), "Temp layer must not exist before the test.");

            int addedIndex = -1;
            try
            {
                // Act: add via the handler (writes through SerializedObject on TagManager.asset).
                var r = LayerManagementHandler.HandleCommand("add",
                    new JObject { ["action"] = "add", ["layerName"] = TempLayerName });

                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);
                Assert.AreEqual(TempLayerName, (string)payload["layerName"]);
                addedIndex = (int)payload["layerIndex"];
                Assert.GreaterOrEqual(addedIndex, 8, "New user layers are allocated at index >= 8.");

                // AFTERMATH: re-read via LayerMask.LayerToName / NameToLayer (a DIFFERENT path than
                // the SerializedObject write) and assert the real effect at the reported index.
                Assert.AreEqual(TempLayerName, LayerMask.LayerToName(addedIndex),
                    "Layer name should resolve at the reported index via LayerMask.LayerToName.");
                Assert.AreEqual(addedIndex, LayerMask.NameToLayer(TempLayerName),
                    "LayerMask.NameToLayer should round-trip the new layer to its index.");

                // Unrelated reserved layers did NOT move.
                Assert.AreEqual(layer0Before, LayerMask.LayerToName(0), "Layer 0 (Default) must be unchanged.");
                Assert.AreEqual(layer1Before, LayerMask.LayerToName(1), "Layer 1 (TransparentFX) must be unchanged.");
                Assert.AreEqual(layer5Before, LayerMask.LayerToName(5), "Layer 5 (UI) must be unchanged.");
            }
            finally
            {
                // Restore: clear the temp layer slot.
                if (FindLayerIndex(TempLayerName) != -1)
                {
                    LayerManagementHandler.RemoveLayer(TempLayerName);
                }
                Assert.AreEqual(-1, FindLayerIndex(TempLayerName), "Cleanup failed: temp layer still present.");
            }
        }

        [Test]
        public void ManageLayers_Remove_RealLayerDisappears_ReservedUnchanged()
        {
            // Arrange: ensure the temp layer exists first (via the add handler), and capture its index.
            if (FindLayerIndex(TempLayerName) == -1)
            {
                var addOutcome = LayerManagementHandler.AddLayer(TempLayerName);
                Assert.IsFalse(addOutcome.IsError, addOutcome.Error);
            }
            int index = FindLayerIndex(TempLayerName);
            Assert.AreNotEqual(-1, index, "Arrange failed: temp layer should exist before remove.");

            string layer0Before = LayerMask.LayerToName(0);
            string layer5Before = LayerMask.LayerToName(5);

            bool removed = false;
            try
            {
                // Act: remove via the handler.
                var r = LayerManagementHandler.HandleCommand("remove",
                    new JObject { ["action"] = "remove", ["layerName"] = TempLayerName });

                Assert.IsFalse(r.IsError, r.Error);
                removed = true;

                // AFTERMATH: independently confirm the slot is empty and the name no longer resolves.
                Assert.IsTrue(string.IsNullOrEmpty(LayerMask.LayerToName(index)),
                    "The layer slot should be empty after remove.");
                Assert.AreEqual(-1, LayerMask.NameToLayer(TempLayerName),
                    "LayerMask.NameToLayer should no longer resolve the removed layer.");

                // Unrelated reserved layers did NOT move.
                Assert.AreEqual(layer0Before, LayerMask.LayerToName(0), "Layer 0 (Default) must be unchanged.");
                Assert.AreEqual(layer5Before, LayerMask.LayerToName(5), "Layer 5 (UI) must be unchanged.");
            }
            finally
            {
                // Restore: if remove did not complete, clean up the residue.
                if (!removed && FindLayerIndex(TempLayerName) != -1)
                {
                    LayerManagementHandler.RemoveLayer(TempLayerName);
                }
                Assert.AreEqual(-1, FindLayerIndex(TempLayerName), "Cleanup failed: temp layer still present.");
            }
        }
    }
}
