using UnityEngine;

namespace UnityEditorMCP.Tests
{
    public enum SerFixtureEnum { Alpha, Beta, Gamma }

    // Drives the serialized-property tests. The PRIVATE [SerializeField] is the headline (D6).
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

    public class SerFixtureComponent : MonoBehaviour
    {
        public int Hp = 100;
        [SerializeField] private float speed = 5f;
        public float ReadSpeed() => speed;
    }
}
