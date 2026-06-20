using System;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    /// <summary>Canonical JSON ↔ SerializedProperty, identical for inspect output / set value / expected
    /// (read-write symmetry). Floor-safe: typed per-type accessors only (no 2022.2 boxedValue).</summary>
    public static class SerializedValue
    {
        public static JToken Read(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return p.doubleValue;
                case SerializedPropertyType.String: return p.stringValue ?? "";
                case SerializedPropertyType.Character: return p.intValue;
                case SerializedPropertyType.LayerMask: return p.intValue;
                case SerializedPropertyType.ArraySize: return p.intValue;
                case SerializedPropertyType.Enum:
                    return (p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length)
                        ? (JToken)p.enumNames[p.enumValueIndex] : p.enumValueIndex;
                case SerializedPropertyType.Vector2: return V(p.vector2Value.x, p.vector2Value.y);
                case SerializedPropertyType.Vector3: { var v = p.vector3Value; return V(v.x, v.y, v.z); }
                case SerializedPropertyType.Vector4: { var v = p.vector4Value; return V4(v.x, v.y, v.z, v.w); }
                case SerializedPropertyType.Vector2Int: { var v = p.vector2IntValue; return V(v.x, v.y); }
                case SerializedPropertyType.Vector3Int: { var v = p.vector3IntValue; return V(v.x, v.y, v.z); }
                case SerializedPropertyType.Quaternion: { var q = p.quaternionValue; return V4(q.x, q.y, q.z, q.w); }
                case SerializedPropertyType.Color: { var c = p.colorValue; return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a }; }
                case SerializedPropertyType.Rect: { var r = p.rectValue; return new JObject { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height }; }
                case SerializedPropertyType.Bounds: { var b = p.boundsValue; return new JObject { ["center"] = V(b.center.x, b.center.y, b.center.z), ["size"] = V(b.size.x, b.size.y, b.size.z) }; }
                case SerializedPropertyType.ObjectReference: return RefToken(p.objectReferenceValue);
                case SerializedPropertyType.ManagedReference: return p.managedReferenceFullTypename ?? ""; // read-only in 0.7.0
                case SerializedPropertyType.AnimationCurve: return CurveToken(p.animationCurveValue);     // read-only in 0.7.0
                default: return JValue.CreateNull(); // Gradient + exotic: read-only marker
            }
        }

        // Returns false + a TYPE_MISMATCH-style message on failure; never throws on bad input.
        public static bool Write(SerializedProperty p, JToken v, out string error)
        {
            error = null;
            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer: p.longValue = v.Value<long>(); return true;
                    case SerializedPropertyType.Boolean: p.boolValue = v.Value<bool>(); return true;
                    case SerializedPropertyType.Float: p.doubleValue = v.Value<double>(); return true;
                    case SerializedPropertyType.String: p.stringValue = v.Value<string>() ?? ""; return true;
                    case SerializedPropertyType.Character: p.intValue = v.Value<int>(); return true;
                    case SerializedPropertyType.LayerMask: p.intValue = v.Value<int>(); return true;
                    case SerializedPropertyType.Enum: return WriteEnum(p, v, out error);
                    case SerializedPropertyType.Vector2: p.vector2Value = new Vector2(F(v, "x"), F(v, "y")); return true;
                    case SerializedPropertyType.Vector3: p.vector3Value = new Vector3(F(v, "x"), F(v, "y"), F(v, "z")); return true;
                    case SerializedPropertyType.Vector4: p.vector4Value = new Vector4(F(v, "x"), F(v, "y"), F(v, "z"), F(v, "w")); return true;
                    case SerializedPropertyType.Vector2Int: p.vector2IntValue = new Vector2Int(I(v, "x"), I(v, "y")); return true;
                    case SerializedPropertyType.Vector3Int: p.vector3IntValue = new Vector3Int(I(v, "x"), I(v, "y"), I(v, "z")); return true;
                    case SerializedPropertyType.Quaternion:
                        p.quaternionValue = v["euler"] != null
                            ? Quaternion.Euler(F(v["euler"], "x"), F(v["euler"], "y"), F(v["euler"], "z"))
                            : new Quaternion(F(v, "x"), F(v, "y"), F(v, "z"), F(v, "w"));
                        return true;
                    case SerializedPropertyType.Color: p.colorValue = new Color(F(v, "r"), F(v, "g"), F(v, "b"), v["a"] != null ? F(v, "a") : 1f); return true;
                    case SerializedPropertyType.Rect: p.rectValue = new Rect(F(v, "x"), F(v, "y"), F(v, "width"), F(v, "height")); return true;
                    case SerializedPropertyType.Bounds: p.boundsValue = new Bounds(new Vector3(F(v["center"], "x"), F(v["center"], "y"), F(v["center"], "z")), new Vector3(F(v["size"], "x"), F(v["size"], "y"), F(v["size"], "z"))); return true;
                    case SerializedPropertyType.ObjectReference: return WriteRef(p, v, out error);
                    case SerializedPropertyType.AnimationCurve:
                    {
                        var keys = v["keys"] as JArray ?? new JArray();
                        var frames = new Keyframe[keys.Count];
                        for (int i = 0; i < keys.Count; i++)
                            frames[i] = new Keyframe(F(keys[i], "time"), F(keys[i], "value"), F(keys[i], "inTangent"), F(keys[i], "outTangent"));
                        p.animationCurveValue = new AnimationCurve(frames);
                        return true;
                    }
                    default: error = $"{p.propertyType} is read-only"; return false;
                }
            }
            catch (Exception e) { error = $"TYPE_MISMATCH: cannot write {p.propertyType} from {v?.Type.ToString() ?? "null"} ({e.Message})"; return false; }
        }

        private static bool WriteEnum(SerializedProperty p, JToken v, out string error)
        {
            error = null;
            if (v.Type == JTokenType.Integer)
            {
                var i = v.Value<int>();
                if (i < 0 || i >= p.enumNames.Length) { error = $"TYPE_MISMATCH: enum index {i} out of range [0,{p.enumNames.Length})"; return false; }
                p.enumValueIndex = i; return true;
            }
            var name = v.Value<string>();
            var idx = Array.IndexOf(p.enumNames, name);
            if (idx < 0) { error = $"TYPE_MISMATCH: '{name}' is not a member of the enum"; return false; }
            p.enumValueIndex = idx; return true;
        }

        private static bool WriteRef(SerializedProperty p, JToken v, out string error)
        {
            error = null;
            if (v == null || v.Type == JTokenType.Null) { p.objectReferenceValue = null; return true; } // explicit clear
            var obj = SerializedTargeting.ResolveObjectReference(v as JObject, out error);
            // A provided (non-null) reference that does NOT resolve is an error — never silently clear the field.
            if (obj == null) { if (error == null) error = "TYPE_MISMATCH: object reference did not resolve (pass JSON null to clear)"; return false; }
            p.objectReferenceValue = obj; return true;
        }

        public static JToken RefToken(UnityEngine.Object o)
        {
            if (o == null) return JValue.CreateNull();
            var path = AssetDatabase.GetAssetPath(o);
            var jo = new JObject { ["instanceId"] = o.GetInstanceID(), ["type"] = o.GetType().Name, ["name"] = o.name };
            if (!string.IsNullOrEmpty(path)) { jo["assetPath"] = path; jo["guid"] = AssetDatabase.AssetPathToGUID(path); }
            return jo;
        }

        private static JToken CurveToken(AnimationCurve c)
        {
            var keys = new JArray();
            if (c != null) foreach (var k in c.keys) keys.Add(new JObject { ["time"] = k.time, ["value"] = k.value, ["inTangent"] = k.inTangent, ["outTangent"] = k.outTangent });
            return new JObject { ["keys"] = keys };
        }

        private static JObject V(float x, float y) => new JObject { ["x"] = x, ["y"] = y };
        private static JObject V(float x, float y, float z) => new JObject { ["x"] = x, ["y"] = y, ["z"] = z };
        private static JObject V4(float x, float y, float z, float w) => new JObject { ["x"] = x, ["y"] = y, ["z"] = z, ["w"] = w };
        private static float F(JToken v, string k) => v[k] != null ? v[k].Value<float>() : 0f;
        private static int I(JToken v, string k) => v[k] != null ? v[k].Value<int>() : 0;
    }
}
