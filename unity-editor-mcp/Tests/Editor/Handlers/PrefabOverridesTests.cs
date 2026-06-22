using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // manage_prefab_overrides (E-tail slice 3). Creates a temp prefab + instance in SetUp, removes both in
    // TearDown. Exercises list / revert_property / apply_property + the error paths.
    public class PrefabOverridesTests
    {
        private const string PrefabPath = "Assets/__prefab_ovr_test__.prefab";
        private const string InstanceName = "__PrefabOvrInstance__";
        private GameObject _instance;

        [SetUp]
        public void SetUp()
        {
            var src = new GameObject("__PrefabOvrSrc__");
            PrefabUtility.SaveAsPrefabAsset(src, PrefabPath);
            Object.DestroyImmediate(src);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            _instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            _instance.name = InstanceName;
        }

        [TearDown]
        public void TearDown()
        {
            if (_instance != null) Object.DestroyImmediate(_instance);
            AssetDatabase.DeleteAsset(PrefabPath);
        }

        private void OverridePositionX(float x)
        {
            _instance.transform.localPosition = new Vector3(x, 0, 0);
            PrefabUtility.RecordPrefabInstancePropertyModifications(_instance.transform);
        }

        [Test]
        public void NotAnInstance_InvalidState()
        {
            var plain = new GameObject("__PlainOvrGo__");
            try
            {
                var r = AssetManagementHandler.ManagePrefabOverrides(new JObject
                {
                    ["action"] = "list",
                    ["gameObjectPath"] = "__PlainOvrGo__"
                });
                Assert.IsTrue(r.IsError);
                Assert.AreEqual("INVALID_STATE", r.Code);
            }
            finally { Object.DestroyImmediate(plain); }
        }

        [Test]
        public void List_AfterOverride_ReportsModification()
        {
            OverridePositionX(5f);
            var r = AssetManagementHandler.ManagePrefabOverrides(new JObject
            {
                ["action"] = "list",
                ["gameObjectPath"] = InstanceName
            });
            Assert.IsFalse(r.IsError, r.Error);
            Assert.Greater((int)JObject.FromObject(r.Payload)["propertyModificationCount"], 0);
        }

        [Test]
        public void RevertProperty_RemovesOverride()
        {
            OverridePositionX(7f);
            var r = AssetManagementHandler.ManagePrefabOverrides(new JObject
            {
                ["action"] = "revert_property",
                ["gameObjectPath"] = InstanceName,
                ["componentType"] = "Transform",
                ["propertyPath"] = "m_LocalPosition.x"
            });
            Assert.IsFalse(r.IsError, r.Error);
            Assert.AreEqual(0f, _instance.transform.localPosition.x, 0.0001f);
        }

        [Test]
        public void ApplyProperty_UpdatesSource()
        {
            OverridePositionX(9f);
            var r = AssetManagementHandler.ManagePrefabOverrides(new JObject
            {
                ["action"] = "apply_property",
                ["gameObjectPath"] = InstanceName,
                ["componentType"] = "Transform",
                ["propertyPath"] = "m_LocalPosition.x"
            });
            Assert.IsFalse(r.IsError, r.Error);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.AreEqual(9f, prefab.transform.localPosition.x, 0.0001f);
        }

        [Test]
        public void MissingPropertyPath_ValidationError()
        {
            var r = AssetManagementHandler.ManagePrefabOverrides(new JObject
            {
                ["action"] = "apply_property",
                ["gameObjectPath"] = InstanceName,
                ["componentType"] = "Transform"
            });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("VALIDATION_ERROR", r.Code);
        }

        [Test]
        public void UnknownComponent_NotFound()
        {
            var r = AssetManagementHandler.ManagePrefabOverrides(new JObject
            {
                ["action"] = "apply_property",
                ["gameObjectPath"] = InstanceName,
                ["componentType"] = "NoSuchComponentXyz",
                ["propertyPath"] = "m_Foo"
            });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("NOT_FOUND", r.Code);
        }
    }
}
