using UnityEngine;

namespace UnityEditorMCP.Tests
{
    public enum SerFixtureEnum { Alpha, Beta, Gamma }

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

        public float ReadPrivateFloat() => privateFloat; // test-only proof the private write landed
    }
}
