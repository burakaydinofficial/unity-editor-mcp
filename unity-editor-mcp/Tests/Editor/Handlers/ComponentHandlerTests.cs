using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Unit tests for ComponentHandler functionality.
    ///
    /// Handlers return anonymous objects (internal to the Editor assembly), not
    /// Dictionaries, so results are asserted through a JObject view: a missing key
    /// reads as null instead of throwing, and it works across the assembly boundary.
    /// </summary>
    [TestFixture]
    public class ComponentHandlerTests
    {
        private GameObject testGameObject;

        [SetUp]
        public void Setup()
        {
            // Create a test GameObject for each test
            testGameObject = new GameObject("TestObject");
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test GameObject
            if (testGameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(testGameObject);
            }
        }

        #region AddComponent Tests

        [Test]
        public void AddComponent_WithValidType_ShouldSucceed()
        {
            // Arrange
            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "Rigidbody",
                ["properties"] = new JObject
                {
                    ["mass"] = 2.0f,
                    ["useGravity"] = true
                }
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.AddComponent(parameters));

            // Assert
            Assert.IsFalse(dict.ContainsKey("error"));
            Assert.IsTrue(dict.ContainsKey("success"));
            Assert.IsTrue((bool)dict["success"]);

            // Verify component was added
            var rb = testGameObject.GetComponent<Rigidbody>();
            Assert.IsNotNull(rb);
            Assert.AreEqual(2.0f, rb.mass);
            Assert.IsTrue(rb.useGravity);
        }

        [Test]
        public void AddComponent_WithInvalidType_ShouldFail()
        {
            // Arrange
            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "NonExistentComponent"
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.AddComponent(parameters));

            // Assert
            Assert.IsTrue(dict.ContainsKey("error"));
            Assert.IsTrue(dict["error"].ToString().Contains("Component type not found"));
        }

        [Test]
        public void AddComponent_ToNonExistentGameObject_ShouldFail()
        {
            // Arrange
            var parameters = new JObject
            {
                ["gameObjectPath"] = "/NonExistentObject",
                ["componentType"] = "Rigidbody"
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.AddComponent(parameters));

            // Assert
            Assert.IsTrue(dict.ContainsKey("error"));
            Assert.IsTrue(dict["error"].ToString().Contains("GameObject not found"));
        }

        [Test]
        public void AddComponent_DuplicateUniqueComponent_ShouldFail()
        {
            // Arrange
            testGameObject.AddComponent<Rigidbody>(); // Add first

            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "Rigidbody"
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.AddComponent(parameters));

            // Assert
            Assert.IsTrue(dict.ContainsKey("error"));
            Assert.IsTrue(dict["error"].ToString().Contains("already has component"));
        }

        #endregion

        #region RemoveComponent Tests

        [Test]
        public void RemoveComponent_ExistingComponent_ShouldSucceed()
        {
            // Arrange
            testGameObject.AddComponent<Rigidbody>();
            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "Rigidbody"
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.RemoveComponent(parameters));

            // Assert
            Assert.IsFalse(dict.ContainsKey("error"));
            Assert.IsTrue((bool)dict["removed"]);
            Assert.IsNull(testGameObject.GetComponent<Rigidbody>());
        }

        [Test]
        public void RemoveComponent_NonExistentComponent_ShouldReturnFalse()
        {
            // Arrange
            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "Rigidbody"
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.RemoveComponent(parameters));

            // Assert
            Assert.IsFalse(dict.ContainsKey("error"));
            Assert.IsFalse((bool)dict["removed"]);
        }

        [Test]
        public void RemoveComponent_Transform_ShouldFail()
        {
            // Arrange
            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "Transform"
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.RemoveComponent(parameters));

            // Assert
            Assert.IsTrue(dict.ContainsKey("error"));
            Assert.IsTrue(dict["error"].ToString().Contains("Cannot remove Transform"));
        }

        [Test]
        public void RemoveComponent_WithIndex_ShouldRemoveSpecificInstance()
        {
            // Arrange
            testGameObject.AddComponent<BoxCollider>();
            testGameObject.AddComponent<BoxCollider>();

            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "BoxCollider",
                ["componentIndex"] = 1
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.RemoveComponent(parameters));

            // Assert
            Assert.IsTrue((bool)dict["removed"]);
            var remainingColliders = testGameObject.GetComponents<BoxCollider>();
            Assert.AreEqual(1, remainingColliders.Length);
        }

        #endregion

        #region ModifyComponent Tests

        [Test]
        public void ModifyComponent_ValidProperties_ShouldSucceed()
        {
            // Arrange
            var rb = testGameObject.AddComponent<Rigidbody>();
            rb.mass = 1.0f;

            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "Rigidbody",
                ["properties"] = new JObject
                {
                    ["mass"] = 5.0f,
                    ["drag"] = 0.5f
                }
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.ModifyComponent(parameters));

            // Assert
            Assert.IsFalse(dict.ContainsKey("error"));
            Assert.AreEqual(5.0f, rb.mass);
#if UNITY_6000_0_OR_NEWER
            Assert.AreEqual(0.5f, rb.linearDamping);
#else
            Assert.AreEqual(0.5f, rb.drag);
#endif

            var modifiedProps = dict["modifiedProperties"].ToObject<string[]>();
            Assert.Contains("mass", modifiedProps);
            Assert.Contains("drag", modifiedProps);
        }

        [Test]
        public void ModifyComponent_InvalidPropertyType_ShouldFail()
        {
            // Arrange
            testGameObject.AddComponent<Rigidbody>();

            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "Rigidbody",
                ["properties"] = new JObject
                {
                    ["mass"] = "not a number"
                }
            };

            // Act — the handler logs a WARNING for the failed conversion (not an
            // error), so it doesn't trip the Test Runner log-check; no LogAssert needed.
            var dict = TestHelpers.Result(ComponentHandler.ModifyComponent(parameters));

            // Assert
            Assert.IsTrue(dict.ContainsKey("error"));
            Assert.IsTrue(dict["error"].ToString().Contains("not found or invalid"));
        }

        [Test]
        public void ModifyComponent_NonExistentProperty_ShouldFail()
        {
            // Arrange
            testGameObject.AddComponent<Rigidbody>();

            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["componentType"] = "Rigidbody",
                ["properties"] = new JObject
                {
                    ["nonExistentProperty"] = 123
                }
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.ModifyComponent(parameters));

            // Assert
            Assert.IsTrue(dict.ContainsKey("error"));
            Assert.IsTrue(dict["error"].ToString().Contains("Property not found"));
        }

        #endregion

        #region ListComponents Tests

        [Test]
        public void ListComponents_ShouldReturnAllComponents()
        {
            // Arrange
            testGameObject.AddComponent<Rigidbody>();
            testGameObject.AddComponent<BoxCollider>();

            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject"
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.ListComponents(parameters));

            // Assert
            Assert.IsFalse(dict.ContainsKey("error"));

            var components = (JArray)dict["components"];
            Assert.IsNotNull(components);
            Assert.AreEqual(3, components.Count); // Transform + Rigidbody + BoxCollider
        }

        [Test]
        public void ListComponents_WithProperties_ShouldIncludeValues()
        {
            // Arrange
            var rb = testGameObject.AddComponent<Rigidbody>();
            rb.mass = 2.5f;
            rb.useGravity = false;

            var parameters = new JObject
            {
                ["gameObjectPath"] = "/TestObject",
                ["includeProperties"] = true
            };

            // Act
            var dict = TestHelpers.Result(ComponentHandler.ListComponents(parameters));

            // Assert
            var components = (JArray)dict["components"];

            // Find Rigidbody in results
            JObject rbComponent = null;
            foreach (var comp in components)
            {
                var compObj = (JObject)comp;
                if ((string)compObj["type"] == "Rigidbody")
                {
                    rbComponent = compObj;
                    break;
                }
            }

            Assert.IsNotNull(rbComponent);
            Assert.IsTrue(rbComponent.ContainsKey("properties"));

            var props = (JObject)rbComponent["properties"];
            Assert.AreEqual(2.5f, (float)props["mass"]);
            Assert.AreEqual(false, (bool)props["useGravity"]);
        }

        #endregion

        #region Helper Method Tests

        [Test]
        public void ResolveComponentType_ShortName_ShouldResolve()
        {
            // Act
            var type = ComponentHandler.ResolveComponentType("Rigidbody");

            // Assert
            Assert.IsNotNull(type);
            Assert.AreEqual(typeof(Rigidbody), type);
        }

        [Test]
        public void ResolveComponentType_FullName_ShouldResolve()
        {
            // Act
            var type = ComponentHandler.ResolveComponentType("UnityEngine.Rigidbody");

            // Assert
            Assert.IsNotNull(type);
            Assert.AreEqual(typeof(Rigidbody), type);
        }

        [Test]
        public void ConvertValue_SimpleTypes_ShouldConvert()
        {
            // Act & Assert
            Assert.AreEqual(5.0f, ComponentHandler.ConvertValue("5.0", typeof(float)));
            Assert.AreEqual(10, ComponentHandler.ConvertValue("10", typeof(int)));
            Assert.AreEqual(true, ComponentHandler.ConvertValue("true", typeof(bool)));
            Assert.AreEqual("test", ComponentHandler.ConvertValue("test", typeof(string)));
        }

        [Test]
        public void ConvertValue_UnityTypes_ShouldConvert()
        {
            // Arrange
            var vector3Json = new JObject { ["x"] = 1, ["y"] = 2, ["z"] = 3 };
            var colorJson = new JObject { ["r"] = 1, ["g"] = 0, ["b"] = 0, ["a"] = 1 };

            // Act
            var vector = ComponentHandler.ConvertValue(vector3Json, typeof(Vector3));
            var color = ComponentHandler.ConvertValue(colorJson, typeof(Color));

            // Assert
            Assert.AreEqual(new Vector3(1, 2, 3), vector);
            Assert.AreEqual(Color.red, color);
        }

        #endregion
    }
}
