using UnityEngine;

namespace UnityEditorMCP.Tests
{
    public enum SerFixtureEnum { Alpha, Beta, Gamma }

    public interface ISerStrategy { }
    [System.Serializable] public class SerStrategyA : ISerStrategy { public int A = 1; }
    [System.Serializable] public class SerStrategyB : ISerStrategy { public string B = "x"; }

    // Drives the serialized-property tests. The PRIVATE [SerializeField] is the headline (D6).
    // Must be in a file named SerFixtureAsset.cs so Unity creates its MonoScript (needed to serialize to assets).
    public class SerFixtureAsset : ScriptableObject
    {
        public int IntField = 7;
        [SerializeField] private float privateFloat = 1.5f; // headline: reachable by SerializedObject, not reflection
        public bool BoolField = true;
        public string StringField = "hello";
        public Vector3 Vec3Field = new Vector3(1, 2, 3);
        public Color ColorField = Color.red;
        public LayerMask MaskField = 0;
        public SerFixtureEnum EnumField = SerFixtureEnum.Beta;
        public Object RefField;            // ObjectReference
        public int[] IntArray = { 1, 2, 3 };
        public Object[] RefArray;          // object-reference array (the double-delete quirk)
        public Vector2 Vec2Field = new Vector2(1, 2);
        public Vector4 Vec4Field = new Vector4(1, 2, 3, 4);
        public Vector2Int Vec2IntField = new Vector2Int(1, 2);
        public Vector3Int Vec3IntField = new Vector3Int(1, 2, 3);
        public Quaternion QuatField = new Quaternion(0, 0, 0, 1);
        public Rect RectField = new Rect(1, 2, 3, 4);
        public Bounds BoundsField = new Bounds(new Vector3(0, 0, 0), new Vector3(1, 1, 1));
        public char CharField = 'A';
        public AnimationCurve CurveField = AnimationCurve.Linear(0, 0, 1, 1);
        public Gradient GradientField = new Gradient();
        [SerializeReference] public ISerStrategy Strategy;

        public float ReadPrivateFloat() => privateFloat; // test-only proof the private write landed
    }
}
