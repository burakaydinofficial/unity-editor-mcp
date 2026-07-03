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
                    // A single member -> its name; a combined [Flags]/out-of-range value (enumValueIndex == -1) -> the
                    // UNDERLYING int, so a round-tripped read reproduces the value for the CAS. (Bug hunt Flags.)
                    return (p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length)
                        ? (JToken)p.enumNames[p.enumValueIndex] : p.intValue;
                case SerializedPropertyType.Vector2: return V(p.vector2Value.x, p.vector2Value.y);
                case SerializedPropertyType.Vector3: { var v = p.vector3Value; return V(v.x, v.y, v.z); }
                case SerializedPropertyType.Vector4: { var v = p.vector4Value; return V4(v.x, v.y, v.z, v.w); }
                case SerializedPropertyType.Vector2Int: { var v = p.vector2IntValue; return V(v.x, v.y); }
                case SerializedPropertyType.Vector3Int: { var v = p.vector3IntValue; return V(v.x, v.y, v.z); }
                case SerializedPropertyType.Quaternion: { var q = p.quaternionValue; return V4(q.x, q.y, q.z, q.w); }
                case SerializedPropertyType.Color: return ColorObj(p.colorValue);
                case SerializedPropertyType.Rect: { var r = p.rectValue; return new JObject { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height }; }
                case SerializedPropertyType.Bounds: { var b = p.boundsValue; return new JObject { ["center"] = V(b.center.x, b.center.y, b.center.z), ["size"] = V(b.size.x, b.size.y, b.size.z) }; }
                case SerializedPropertyType.ObjectReference: return RefToken(p.objectReferenceValue);
                case SerializedPropertyType.ManagedReference: return p.managedReferenceFullTypename ?? ""; // read-only in 0.7.0
                case SerializedPropertyType.AnimationCurve: return CurveToken(p.animationCurveValue);
                case SerializedPropertyType.Gradient: return GradientToken(GradientReflection.Get(p));
                default: return JValue.CreateNull(); // exotic types: read-only marker
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
                    case SerializedPropertyType.Integer:
                    {
                        long lv = v.Value<long>();
                        // A 32-bit int backing field silently TRUNCATES a long -> false success. Reject out-of-range. (Bug hunt int.)
                        if (p.type == "int" && (lv < int.MinValue || lv > int.MaxValue))
                        { error = $"TYPE_MISMATCH: {lv} is out of Int32 range for an int field"; return false; }
                        p.longValue = lv; return true;
                    }
                    case SerializedPropertyType.Boolean: p.boolValue = v.Value<bool>(); return true;
                    case SerializedPropertyType.Float: p.doubleValue = v.Value<double>(); return true;
                    case SerializedPropertyType.String: p.stringValue = v.Value<string>() ?? ""; return true;
                    case SerializedPropertyType.Character: p.intValue = v.Value<int>(); return true;
                    case SerializedPropertyType.LayerMask: p.intValue = v.Value<int>(); return true;
                    case SerializedPropertyType.Enum: return WriteEnum(p, v, out error);
                    // Composite writes MERGE with the current value — an omitted component keeps its current value
                    // instead of being zero-filled (a partial update like {x:9} must not destroy y/z). (Bug hunt composite.)
                    case SerializedPropertyType.Vector2: { var c = p.vector2Value; p.vector2Value = new Vector2(F(v, "x", c.x), F(v, "y", c.y)); return true; }
                    case SerializedPropertyType.Vector3: { var c = p.vector3Value; p.vector3Value = new Vector3(F(v, "x", c.x), F(v, "y", c.y), F(v, "z", c.z)); return true; }
                    case SerializedPropertyType.Vector4: { var c = p.vector4Value; p.vector4Value = new Vector4(F(v, "x", c.x), F(v, "y", c.y), F(v, "z", c.z), F(v, "w", c.w)); return true; }
                    case SerializedPropertyType.Vector2Int: { var c = p.vector2IntValue; p.vector2IntValue = new Vector2Int(I(v, "x", c.x), I(v, "y", c.y)); return true; }
                    case SerializedPropertyType.Vector3Int: { var c = p.vector3IntValue; p.vector3IntValue = new Vector3Int(I(v, "x", c.x), I(v, "y", c.y), I(v, "z", c.z)); return true; }
                    case SerializedPropertyType.Quaternion:
                        if (v["euler"] != null) { p.quaternionValue = Quaternion.Euler(F(v["euler"], "x"), F(v["euler"], "y"), F(v["euler"], "z")); }
                        else { var c = p.quaternionValue; p.quaternionValue = new Quaternion(F(v, "x", c.x), F(v, "y", c.y), F(v, "z", c.z), F(v, "w", c.w)); }
                        return true;
                    case SerializedPropertyType.Color: { var c = p.colorValue; p.colorValue = new Color(F(v, "r", c.r), F(v, "g", c.g), F(v, "b", c.b), F(v, "a", c.a)); return true; }
                    case SerializedPropertyType.Rect: { var c = p.rectValue; p.rectValue = new Rect(F(v, "x", c.x), F(v, "y", c.y), F(v, "width", c.width), F(v, "height", c.height)); return true; }
                    case SerializedPropertyType.Bounds: { var c = p.boundsValue; var ce = c.center; var sz = c.size; var cv = v["center"]; var sv = v["size"]; p.boundsValue = new Bounds(new Vector3(F(cv, "x", ce.x), F(cv, "y", ce.y), F(cv, "z", ce.z)), new Vector3(F(sv, "x", sz.x), F(sv, "y", sz.y), F(sv, "z", sz.z))); return true; }
                    case SerializedPropertyType.ObjectReference: return WriteRef(p, v, out error);
                    case SerializedPropertyType.ManagedReference: return ManagedReferenceResolver.TrySet(p, v, out error);
                    case SerializedPropertyType.AnimationCurve:
                    {
                        var keys = v["keys"] as JArray ?? new JArray();
                        var frames = new Keyframe[keys.Count];
                        for (int i = 0; i < keys.Count; i++)
                            frames[i] = new Keyframe(F(keys[i], "time"), F(keys[i], "value"), F(keys[i], "inTangent"), F(keys[i], "outTangent"));
                        p.animationCurveValue = new AnimationCurve(frames);
                        return true;
                    }
                    case SerializedPropertyType.Gradient:
                    {
                        var g = new Gradient();
                        var cks = new System.Collections.Generic.List<GradientColorKey>();
                        foreach (var k in (v["colorKeys"] as JArray ?? new JArray())) cks.Add(new GradientColorKey(ColorFrom(k["color"]), F(k, "time")));
                        var aks = new System.Collections.Generic.List<GradientAlphaKey>();
                        foreach (var k in (v["alphaKeys"] as JArray ?? new JArray())) aks.Add(new GradientAlphaKey(F(k, "alpha"), F(k, "time")));
                        g.SetKeys(cks.ToArray(), aks.ToArray());
                        if (v["mode"] != null && Enum.TryParse<GradientMode>(v["mode"].ToString(), out var gm)) g.mode = gm;
                        if (!GradientReflection.Set(p, g)) { error = "TYPE_MISMATCH: gradientValue not accessible on this Unity version"; return false; }
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
                // The integer is the UNDERLYING enum value (NOT the enumValueIndex ORDINAL — writing the ordinal
                // silently mis-stored [Flags] bitmasks and non-sequential enums). Validate it against the reflected
                // enum type when resolvable: exact member for plain enums, defined-bits subset for [Flags]; an
                // unresolvable type skips validation. intValue is floor-safe. (Bug hunt Flags.)
                var i = v.Value<int>();
                var enumType = GetEnumFieldType(p);
                if (enumType != null && !IsValidEnumValue(enumType, i))
                { error = $"TYPE_MISMATCH: {i} is not a valid value of {enumType.Name}"; return false; }
                p.intValue = i;
                return true;
            }
            var name = v.Value<string>();
            var idx = Array.IndexOf(p.enumNames, name);
            if (idx < 0) { error = $"TYPE_MISMATCH: '{name}' is not a member of the enum"; return false; }
            p.enumValueIndex = idx; return true;
        }

        private static bool IsValidEnumValue(Type enumType, int value)
        {
            foreach (var ev in Enum.GetValues(enumType))
                if (Convert.ToInt64(ev) == value) return true; // exact member (any underlying type)
            if (enumType.GetCustomAttributes(typeof(FlagsAttribute), false).Length > 0)
            {
                long mask = 0;
                foreach (var ev in Enum.GetValues(enumType)) mask |= Convert.ToInt64(ev);
                return (value & ~mask) == 0; // any combination of defined bits, INCLUDING 0 (clear all flags). (Bug hunt: flags-0.)
            }
            return false;
        }

        private static bool WriteRef(SerializedProperty p, JToken v, out string error)
        {
            error = null;
            if (v == null || v.Type == JTokenType.Null) { p.objectReferenceValue = null; return true; } // explicit clear
            var obj = SerializedTargeting.ResolveObjectReference(v as JObject, out error);
            // A provided (non-null) reference that does NOT resolve is an error — never silently clear the field.
            if (obj == null) { if (error == null) error = "TYPE_MISMATCH: object reference did not resolve (pass JSON null to clear)"; return false; }
            // objectReferenceValue does NO type validation on assign, so a wrong-typed object is reported as success
            // but ends up null on reserialize. Reject it when we can resolve the field's declared reference type. (Bug hunt objref.)
            var fieldType = GetObjectFieldType(p);
            if (fieldType != null && !fieldType.IsInstanceOfType(obj))
            {
                error = $"TYPE_MISMATCH: {obj.GetType().Name} is not assignable to the field's {fieldType.Name} reference";
                return false;
            }
            p.objectReferenceValue = obj; return true;
        }

        // Resolves a SerializedProperty's declared field TYPE by reflecting the target type along the property path.
        // Returns null when unresolvable (callers then skip their type check).
        private static Type GetFieldTypeByPath(SerializedProperty p)
        {
            try
            {
                Type t = p.serializedObject.targetObject.GetType();
                foreach (var raw in p.propertyPath.Replace(".Array.data[", "[").Split('.'))
                {
                    var name = raw.Contains("[") ? raw.Substring(0, raw.IndexOf('[')) : raw;
                    var fi = GetFieldRecursive(t, name);
                    if (fi == null) return null;
                    t = fi.FieldType;
                    if (raw.Contains("[")) // array/list element
                        t = t.IsArray ? t.GetElementType() : (t.IsGenericType ? t.GetGenericArguments()[0] : t);
                }
                return t;
            }
            catch { return null; }
        }

        private static Type GetObjectFieldType(SerializedProperty p)
        {
            var t = GetFieldTypeByPath(p);
            return (t != null && typeof(UnityEngine.Object).IsAssignableFrom(t)) ? t : null;
        }

        private static Type GetEnumFieldType(SerializedProperty p)
        {
            var t = GetFieldTypeByPath(p);
            return (t != null && t.IsEnum) ? t : null;
        }

        private static System.Reflection.FieldInfo GetFieldRecursive(Type t, string name)
        {
            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            for (var cur = t; cur != null; cur = cur.BaseType) { var fi = cur.GetField(name, BF); if (fi != null) return fi; }
            return null;
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
        private static float F(JToken v, string k) => v?[k] != null ? v[k].Value<float>() : 0f;
        private static float F(JToken v, string k, float fallback) => v?[k] != null ? v[k].Value<float>() : fallback;
        private static int I(JToken v, string k) => v?[k] != null ? v[k].Value<int>() : 0;
        private static int I(JToken v, string k, int fallback) => v?[k] != null ? v[k].Value<int>() : fallback;
        private static JObject ColorObj(Color c) => new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
        private static Color ColorFrom(JToken v) => new Color(F(v, "r"), F(v, "g"), F(v, "b"), v["a"] != null ? F(v, "a") : 1f);

        private static JToken GradientToken(Gradient g)
        {
            if (g == null) return JValue.CreateNull();
            var ck = new JArray(); foreach (var k in g.colorKeys) ck.Add(new JObject { ["color"] = ColorObj(k.color), ["time"] = k.time });
            var ak = new JArray(); foreach (var k in g.alphaKeys) ak.Add(new JObject { ["alpha"] = k.alpha, ["time"] = k.time });
            return new JObject { ["colorKeys"] = ck, ["alphaKeys"] = ak, ["mode"] = g.mode.ToString() };
        }

        // gradientValue is internal until Unity 2022.2 — reflection access works on the 2020.3 floor + newer
        // (COMPATIBILITY.md: the only reflection workaround in the serialization core).
        private static class GradientReflection
        {
            private static readonly System.Reflection.PropertyInfo Prop =
                typeof(SerializedProperty).GetProperty("gradientValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            public static Gradient Get(SerializedProperty p) => Prop?.GetValue(p) as Gradient;
            public static bool Set(SerializedProperty p, Gradient g) { if (Prop == null) return false; Prop.SetValue(p, g); return true; }
        }
    }
}
