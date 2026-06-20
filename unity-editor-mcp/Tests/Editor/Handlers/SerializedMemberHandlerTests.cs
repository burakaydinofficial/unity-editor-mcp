using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    public class SerializedMemberHandlerTests
    {
        private string _assetPath;
        private SerFixtureAsset _asset;
        [SetUp] public void Setup()
        {
            _asset = ScriptableObject.CreateInstance<SerFixtureAsset>();
            _assetPath = "Assets/__sertest__.asset";
            AssetDatabase.CreateAsset(_asset, _assetPath);
        }
        [TearDown] public void Teardown() { AssetDatabase.DeleteAsset(_assetPath); }

        [Test] public void Inspect_AssetByPath_ReturnsTreeWithValuesAndTypes()
        {
            var outcome = SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath } });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            var props = (JArray)data["object"]["properties"];
            var intField = FindProp(props, "IntField");
            Assert.IsNotNull(intField);
            Assert.AreEqual("Integer", (string)intField["propertyType"]);
            Assert.AreEqual(7L, (long)intField["value"]);
            Assert.IsNotNull(FindProp(props, "privateFloat"), "private [SerializeField] must appear");
        }

        [Test] public void Inspect_StructField_EmitsCompositeValue()
        {
            var data = JObject.FromObject(SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath } }).Payload);
            var vec = FindProp((JArray)data["object"]["properties"], "Vec3Field");
            Assert.IsNotNull(vec);
            Assert.AreEqual("Vector3", (string)vec["propertyType"]);
            Assert.AreEqual(1f, (float)vec["value"]["x"]); // composite emitted as a unit, not recursed into x/y/z
            Assert.AreEqual(2f, (float)vec["value"]["y"]);
            Assert.AreEqual(3f, (float)vec["value"]["z"]);
        }

        [Test] public void Set_PrivateSerializeField_Writes_Headline()
        {
            var read = SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath } });
            var props = (JArray)JObject.FromObject(read.Payload)["object"]["properties"];
            var cur = FindProp(props, "privateFloat")["value"];
            var outcome = SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["privateFloat"] = new JObject { ["value"] = 42.0, ["expected"] = cur } } } } });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            Assert.AreEqual(42f, _asset.ReadPrivateFloat(), 0.0001f); // the private field actually changed
        }

        [Test] public void Set_CasToleratesIntVsFloatNumberForms()
        {
            // Vec3Field is {1,2,3} as floats; an agent echoing {1,2,3} as JSON integers must still CAS-match.
            var outcome = SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["Vec3Field"] = new JObject {
                                  ["value"] = new JObject { ["x"] = 4, ["y"] = 5, ["z"] = 6 },
                                  ["expected"] = new JObject { ["x"] = 1, ["y"] = 2, ["z"] = 3 } } } } } }); // expected as ints
            Assert.IsFalse(outcome.IsError, outcome.Error);
            Assert.AreEqual(1, ((JArray)JObject.FromObject(outcome.Payload)["changed"]).Count, "int-form expected must match the float-valued read");
            Assert.AreEqual(new Vector3(4, 5, 6), _asset.Vec3Field);
        }

        [Test] public void Set_StaleExpected_SkipsWithStale()
        {
            var outcome = SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 99, ["expected"] = 12345 } } } } }); // wrong expected
            var data = JObject.FromObject(outcome.Payload);
            Assert.AreEqual(0, ((JArray)data["changed"]).Count);
            Assert.AreEqual("STALE", (string)((JArray)data["skipped"])[0]["code"]);
            Assert.AreEqual(7, _asset.IntField); // unchanged
        }

        [Test] public void Set_MissingExpected_RefusedUnlessForce()
        {
            var noForce = SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 5 } } } } });
            Assert.AreEqual("MISSING_PRECONDITION", (string)((JArray)JObject.FromObject(noForce.Payload)["skipped"])[0]["code"]);
            var forced = SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 5 } } } } });
            Assert.IsFalse(forced.IsError, forced.Error);
            Assert.AreEqual(5, _asset.IntField);
        }

        // Selector + prefab tests use a built-in runtime component (BoxCollider) — a custom MonoBehaviour in the
        // Editor test assembly is an "editor script" and cannot be attached to a GameObject.

        [Test] public void SetBySelector_PreviewThenCommit_AppliesToAllMatched()
        {
            var a = new GameObject("A", typeof(BoxCollider));
            var b = new GameObject("B", typeof(BoxCollider));
            try
            {
                var match = new JObject { ["match"] = new JObject { ["componentType"] = "BoxCollider" },
                                          ["component"] = "BoxCollider",
                                          ["set"] = new JObject { ["m_IsTrigger"] = true } };
                var preview = JObject.FromObject(SerializedMemberHandler.Set((JObject)match.DeepClone()).Payload);
                Assert.IsFalse((bool)preview["applied"]);
                Assert.IsNotNull(preview["token"]);
                Assert.AreEqual(2, ((JArray)preview["objects"]).Count);

                var commit = (JObject)match.DeepClone(); commit["token"] = preview["token"];
                var done = SerializedMemberHandler.Set(commit);
                Assert.IsFalse(done.IsError, done.Error);
                Assert.IsTrue(a.GetComponent<BoxCollider>().isTrigger);
                Assert.IsTrue(b.GetComponent<BoxCollider>().isTrigger);
            }
            finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
        }

        [Test] public void SetBySelector_StaleToken_Rejected()
        {
            var a = new GameObject("A", typeof(BoxCollider));
            try
            {
                var match = new JObject { ["match"] = new JObject { ["componentType"] = "BoxCollider" }, ["component"] = "BoxCollider", ["set"] = new JObject { ["m_IsTrigger"] = true } };
                var preview = JObject.FromObject(SerializedMemberHandler.Set((JObject)match.DeepClone()).Payload);
                a.GetComponent<BoxCollider>().isTrigger = true; // state changes after preview
                var commit = (JObject)match.DeepClone(); commit["token"] = preview["token"];
                var done = SerializedMemberHandler.Set(commit);
                Assert.IsTrue(done.IsError);
                Assert.AreEqual("STALE_MATCH", done.Code);
            }
            finally { Object.DestroyImmediate(a); }
        }

        [Test] public void Set_PrefabInstance_RecordsOverrideAndKeepsLink()
        {
            var src = new GameObject("PfxSrc", typeof(BoxCollider));
            var prefabPath = "Assets/__serpfx__.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(src, prefabPath);
            Object.DestroyImmediate(src);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var comp = instance.GetComponent<BoxCollider>();
                var outcome = SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray {
                    new JObject { ["target"] = new JObject { ["instanceId"] = comp.GetInstanceID() },
                                  ["set"] = new JObject { ["m_IsTrigger"] = new JObject { ["value"] = true } } } } });
                Assert.IsFalse(outcome.IsError, outcome.Error);
                Assert.IsTrue(comp.isTrigger);
                Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(comp), "prefab link preserved");
                var mods = PrefabUtility.GetPropertyModifications(instance);
                Assert.IsTrue(mods != null && System.Array.Exists(mods, m => m.propertyPath == "m_IsTrigger"), "m_IsTrigger recorded as a prefab override");
            }
            finally { Object.DestroyImmediate(instance); AssetDatabase.DeleteAsset(prefabPath); }
        }

        [Test] public void Set_NonObjectSpec_SkipsValidationError()
        {
            var outcome = SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["IntField"] = 42 } } } }); // bare value, not {value,expected}
            Assert.AreEqual("VALIDATION_ERROR", (string)((JArray)JObject.FromObject(outcome.Payload)["skipped"])[0]["code"]);
            Assert.AreEqual(7, _asset.IntField); // unchanged
        }

        [Test] public void Set_ObjectReferenceNotFound_TypeMismatchNotSilentNull()
        {
            var outcome = SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["RefField"] = new JObject { ["value"] = new JObject { ["guid"] = "ffffffffffffffffffffffffffffffff" } } } } } });
            var data = JObject.FromObject(outcome.Payload);
            Assert.AreEqual(0, ((JArray)data["changed"]).Count, "an unresolved reference must not write (no silent null)");
            Assert.AreEqual("TYPE_MISMATCH", (string)((JArray)data["skipped"])[0]["code"]);
        }

        [Test] public void SetBySelector_TypeMismatch_SurfacedNotSilent()
        {
            var a = new GameObject("A", typeof(BoxCollider));
            try
            {
                var outcome = SerializedMemberHandler.Set(new JObject { ["force"] = true,
                    ["match"] = new JObject { ["componentType"] = "BoxCollider" }, ["component"] = "BoxCollider",
                    ["set"] = new JObject { ["m_IsTrigger"] = "not-a-bool" } }); // bool field, string value
                var data = JObject.FromObject(outcome.Payload);
                Assert.AreEqual(0, ((JArray)data["changed"]).Count);
                Assert.AreEqual("TYPE_MISMATCH", (string)((JArray)data["skipped"])[0]["code"]);
            }
            finally { Object.DestroyImmediate(a); }
        }

        // ---- addressing coverage ----

        [Test] public void Target_ByGuid_Resolves()
        {
            var guid = AssetDatabase.AssetPathToGUID(_assetPath);
            var outcome = SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["guid"] = guid } });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            Assert.IsNotNull(FindProp((JArray)JObject.FromObject(outcome.Payload)["object"]["properties"], "IntField"));
        }

        [Test] public void Target_ByScenePath_Resolves()
        {
            var go = new GameObject("ScenePathTarget", typeof(BoxCollider));
            try
            {
                var outcome = SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["scenePath"] = "/ScenePathTarget" }, ["component"] = "BoxCollider" });
                Assert.IsFalse(outcome.IsError, outcome.Error);
                Assert.IsNotNull(FindProp((JArray)JObject.FromObject(outcome.Payload)["object"]["properties"], "m_IsTrigger"));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void Target_ComponentIndex_Disambiguates()
        {
            var go = new GameObject("MultiCollider");
            go.AddComponent<BoxCollider>();
            go.AddComponent<BoxCollider>(); // two components of the same type
            try
            {
                var ok = SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["scenePath"] = "/MultiCollider" }, ["component"] = "BoxCollider", ["componentIndex"] = 1 });
                Assert.IsFalse(ok.IsError, ok.Error); // index 1 resolves the second
                var miss = SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["scenePath"] = "/MultiCollider" }, ["component"] = "BoxCollider", ["componentIndex"] = 5 });
                Assert.IsTrue(miss.IsError);
                Assert.AreEqual("COMPONENT_NOT_FOUND", miss.Code);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void Selector_Tag_Matches()
        {
            var go = new GameObject("TaggedObj", typeof(BoxCollider)); go.tag = "Player"; // built-in tag
            try
            {
                var outcome = SerializedMemberHandler.Inspect(new JObject { ["match"] = new JObject { ["tag"] = "Player" }, ["component"] = "BoxCollider" });
                Assert.IsFalse(outcome.IsError, outcome.Error);
                Assert.GreaterOrEqual((int)JObject.FromObject(outcome.Payload)["count"], 1);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void Selector_Selection_Matches()
        {
            var go = new GameObject("SelectedObj", typeof(BoxCollider));
            try
            {
                Selection.objects = new Object[] { go };
                var outcome = SerializedMemberHandler.Inspect(new JObject { ["match"] = new JObject { ["selection"] = true }, ["component"] = "BoxCollider" });
                Assert.IsFalse(outcome.IsError, outcome.Error);
                Assert.GreaterOrEqual((int)JObject.FromObject(outcome.Payload)["count"], 1);
            }
            finally { Selection.objects = new Object[0]; Object.DestroyImmediate(go); }
        }

        [Test] public void InspectBySelector_ReturnsMatchedObjects()
        {
            var a = new GameObject("S1", typeof(BoxCollider));
            var b = new GameObject("S2", typeof(BoxCollider));
            try
            {
                var outcome = SerializedMemberHandler.Inspect(new JObject { ["match"] = new JObject { ["componentType"] = "BoxCollider" }, ["component"] = "BoxCollider" });
                Assert.IsFalse(outcome.IsError, outcome.Error);
                Assert.GreaterOrEqual((int)JObject.FromObject(outcome.Payload)["count"], 2);
            }
            finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
        }

        // ---- set options + behavior coverage ----

        [Test] public void Set_DryRun_DoesNotMutate()
        {
            var outcome = SerializedMemberHandler.Set(new JObject { ["dryRun"] = true, ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 555, ["expected"] = 7 } } } } });
            var data = JObject.FromObject(outcome.Payload);
            Assert.IsFalse((bool)data["applied"]);
            Assert.AreEqual(1, ((JArray)data["changed"]).Count); // planned change reported
            Assert.AreEqual(7, _asset.IntField);                  // but not mutated
        }

        [Test] public void Set_AllOrNothing_AbortsOnFirstFailure()
        {
            var outcome = SerializedMemberHandler.Set(new JObject { ["allOrNothing"] = true, ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 1, ["expected"] = 99999 } } } } }); // STALE -> abort
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual(7, _asset.IntField); // nothing written
        }

        [Test] public void Set_MultiTarget_Batch()
        {
            var asset2 = ScriptableObject.CreateInstance<SerFixtureAsset>();
            var path2 = "Assets/__sertest2__.asset";
            AssetDatabase.CreateAsset(asset2, path2);
            try
            {
                var outcome = SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                    new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 10, ["expected"] = 7 } } },
                    new JObject { ["target"] = new JObject { ["assetPath"] = path2 }, ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 20, ["expected"] = 7 } } } } });
                Assert.IsFalse(outcome.IsError, outcome.Error);
                Assert.AreEqual(10, _asset.IntField);
                Assert.AreEqual(20, asset2.IntField);
                Assert.AreEqual(2, ((JArray)JObject.FromObject(outcome.Payload)["changed"]).Count);
            }
            finally { AssetDatabase.DeleteAsset(path2); }
        }

        [Test] public void Set_Undo_RevertsTheChange()
        {
            SerializedMemberHandler.Set(new JObject { ["edits"] = new JArray {
                new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                              ["set"] = new JObject { ["IntField"] = new JObject { ["value"] = 321, ["expected"] = 7 } } } } });
            Assert.AreEqual(321, _asset.IntField);
            Undo.PerformUndo();
            Assert.AreEqual(7, _asset.IntField); // the Undo wiring is real, not just recorded
        }

        [Test] public void SaveAssets_Succeeds()
        {
            var outcome = SerializedMemberHandler.SaveAssets(new JObject());
            Assert.IsFalse(outcome.IsError, outcome.Error);
        }

        internal static JToken FindProp(JArray props, string path)
        {
            foreach (var p in props) if ((string)p["propertyPath"] == path) return p;
            return null;
        }
    }
}
