using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    public class SerializedValueTests
    {
        private SerFixtureAsset _asset;
        private SerializedObject _so;
        [SetUp] public void Setup() { _asset = ScriptableObject.CreateInstance<SerFixtureAsset>(); _so = new SerializedObject(_asset); }
        [TearDown] public void Teardown() { Object.DestroyImmediate(_asset); }

        private void RoundTrip(string path)
        {
            var p = _so.FindProperty(path);
            Assert.IsNotNull(p, $"property {path}");
            var read = SerializedValue.Read(p);
            Assert.IsTrue(SerializedValue.Write(p, read, out var err), err);  // writing what we read must succeed
            _so.ApplyModifiedPropertiesWithoutUndo();
            var p2 = _so.FindProperty(path);
            Assert.IsTrue(JToken.DeepEquals(read, SerializedValue.Read(p2)), $"{path} not stable across round-trip");
        }

        [Test] public void RoundTrips_AllScalarAndStructTypes()
        {
            foreach (var path in new[] { "IntField", "privateFloat", "BoolField", "StringField",
                "Vec3Field", "ColorField", "MaskField", "EnumField" }) RoundTrip(path);
        }

        [Test] public void RoundTrips_RemainingStructAndCharTypes()
        {
            foreach (var path in new[] { "Vec2Field", "Vec4Field", "Vec2IntField", "Vec3IntField",
                "QuatField", "RectField", "BoundsField", "CharField" }) RoundTrip(path);
        }

        [Test] public void Quaternion_WritableByEuler()
        {
            var p = _so.FindProperty("QuatField");
            Assert.IsTrue(SerializedValue.Write(p, JToken.Parse("{\"euler\":{\"x\":0,\"y\":90,\"z\":0}}"), out var err), err);
            _so.ApplyModifiedPropertiesWithoutUndo();
            Assert.AreEqual(90f, _so.FindProperty("QuatField").quaternionValue.eulerAngles.y, 0.01f);
        }

        [Test] public void RoundTrips_ObjectReference()
        {
            var other = ScriptableObject.CreateInstance<SerFixtureAsset>();
            try { _asset.RefField = other; _so.Update(); RoundTrip("RefField"); }
            finally { Object.DestroyImmediate(other); }
        }

        [Test] public void ArrayElement_ReadWriteByPropertyPath()
        {
            var p = _so.FindProperty("IntArray.Array.data[0]");
            Assert.IsNotNull(p);
            Assert.AreEqual(1L, (long)SerializedValue.Read(p));
            Assert.IsTrue(SerializedValue.Write(p, JToken.FromObject(99), out _));
            _so.ApplyModifiedPropertiesWithoutUndo();
            Assert.AreEqual(99, _asset.IntArray[0]);
        }

        [Test] public void AnimationCurve_RoundTrips()
        {
            var p = _so.FindProperty("CurveField");
            var read = SerializedValue.Read(p);
            Assert.IsTrue(SerializedValue.Write(p, read, out var err), err);
            _so.ApplyModifiedPropertiesWithoutUndo();
            Assert.IsTrue(JToken.DeepEquals(read, SerializedValue.Read(_so.FindProperty("CurveField"))));
            Assert.AreEqual(2, _asset.CurveField.keys.Length);
        }

        [Test] public void Gradient_RoundTrips()
        {
            var p = _so.FindProperty("GradientField");
            var read = SerializedValue.Read(p);
            Assert.AreNotEqual(JTokenType.Null, read.Type, "gradient read must work on the floor (reflection)");
            Assert.IsTrue(SerializedValue.Write(p, read, out var err), err);
            _so.ApplyModifiedPropertiesWithoutUndo();
            Assert.IsTrue(JToken.DeepEquals(read, SerializedValue.Read(_so.FindProperty("GradientField"))));
        }

        [Test] public void Write_TypeMismatch_FailsWithCode()
        {
            var p = _so.FindProperty("IntField");
            Assert.IsFalse(SerializedValue.Write(p, JToken.FromObject("not-an-int"), out var err));
            Assert.IsNotNull(err);
        }

        [Test] public void Enum_OutOfRangeIndex_FailsTypeMismatch()
        {
            var p = _so.FindProperty("EnumField");
            Assert.IsFalse(SerializedValue.Write(p, JToken.FromObject(99), out var err)); // index 99 has no enum member
            Assert.IsNotNull(err);
        }

        [Test] public void Enum_WritableByNameAndIndex()
        {
            var p = _so.FindProperty("EnumField");
            Assert.IsTrue(SerializedValue.Write(p, JToken.FromObject("Gamma"), out _)); _so.ApplyModifiedPropertiesWithoutUndo();
            Assert.AreEqual("Gamma", (string)SerializedValue.Read(_so.FindProperty("EnumField")));
            Assert.IsTrue(SerializedValue.Write(_so.FindProperty("EnumField"), JToken.FromObject(0), out _)); _so.ApplyModifiedPropertiesWithoutUndo();
            Assert.AreEqual("Alpha", (string)SerializedValue.Read(_so.FindProperty("EnumField")));
        }
    }
}
