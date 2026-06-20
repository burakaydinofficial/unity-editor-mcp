# Serialization Core (0.7.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the safe `SerializedObject`-based property editor (spec `docs/superpowers/specs/2026-06-20-serialization-core-design.md`): discover a target's serialized tree, then read/write its fields — private `[SerializeField]` included — by id (compare-and-swap) or by selector (preview-token), with Inspector-correct Undo/dirty/prefab/save.

**Architecture:** Three editor commands — `inspect_serialized_object`, `set_serialized_properties`, `save_assets` — in a new `SerializedMemberHandler`, over `SerializedObject`/`SerializedProperty` (never reflection). A per-type canonical JSON value model (`SerializedValue`) gives read/write symmetry; targeting (`SerializedTargeting`) resolves single targets + selectors. Floor-safe (typed accessors, no 2022.2 `boxedValue`).

**Tech Stack:** Unity editor C# 8 / netstandard 2.0; `UnityEditor.SerializedObject`/`Undo`/`PrefabUtility`/`EditorUtility`; Newtonsoft JObject; Core `HandlerOutcome`; NUnit EditMode (the only lane `SerializedObject` runs in).

**Verification loop (every editor change):** via the live bridge — `call_unity_tool(7093, refresh_assets)` → `node scripts/read-editor-log.mjs` (compile gate) → `call_unity_tool(7093, run_tests {testMode:"EditMode", runAll:true})` → `get_test_results {filterStatus:"Failed"}`. Plus `node scripts/compat-lint.mjs` and (Task 5) `node protocol/scripts/check-drift.mjs`.

---

## File structure

- **Create** `unity-editor-mcp/Editor/Handlers/SerializedValue.cs` — per-type canonical JSON ↔ `SerializedProperty` (read + write), with `TYPE_MISMATCH`.
- **Create** `unity-editor-mcp/Editor/Handlers/SerializedTargeting.cs` — resolve a single target descriptor → `(Object, SerializedObject, Component?)`; resolve a `match` selector → list of those.
- **Create** `unity-editor-mcp/Editor/Handlers/SerializedMemberHandler.cs` — the three commands + the tree walk + CAS + the preview-token + correctness semantics.
- **Modify** `unity-editor-mcp/Editor/Core/UnityEditorMCP.cs` — register the three commands.
- **Modify** `protocol/catalog/commands.json` (+ regen `unity-editor-mcp/Core/CommandCatalog.g.cs`).
- **Create** tests: `unity-editor-mcp/Tests/Editor/Handlers/SerializedFixture.cs` (a ScriptableObject + MonoBehaviour fixture), `SerializedValueTests.cs`, `SerializedMemberHandlerTests.cs`.

**Error codes** (all via `HandlerOutcome.Fail(message, code)`): `STALE`, `STALE_MATCH`, `MISSING_PRECONDITION`, `TYPE_MISMATCH`, `TARGET_NOT_FOUND`, `COMPONENT_NOT_FOUND`, `PROPERTY_NOT_FOUND`, `PLAY_MODE`, `VALIDATION_ERROR`.

---

## Task 1: The value model (`SerializedValue`) + round-trip tests

**Files:** Create `SerializedValue.cs`, `Tests/Editor/Handlers/SerializedFixture.cs`, `Tests/Editor/Handlers/SerializedValueTests.cs`

- [ ] **Step 1: Write the fixture**

`unity-editor-mcp/Tests/Editor/Handlers/SerializedFixture.cs`:

```csharp
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
```

- [ ] **Step 2: Write the failing round-trip test**

`unity-editor-mcp/Tests/Editor/Handlers/SerializedValueTests.cs`:

```csharp
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

        [Test] public void Write_TypeMismatch_FailsWithCode()
        {
            var p = _so.FindProperty("IntField");
            Assert.IsFalse(SerializedValue.Write(p, JToken.FromObject("not-an-int"), out var err));
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
```

- [ ] **Step 3: Verify it fails** — `refresh_assets` → `read-editor-log.mjs`: `SerializedValue` undefined.

- [ ] **Step 4: Implement `SerializedValue.cs`**

```csharp
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
                    default: error = $"{p.propertyType} is read-only in 0.7.0"; return false;
                }
            }
            catch (Exception e) { error = $"TYPE_MISMATCH: cannot write {p.propertyType} from {v?.Type.ToString() ?? "null"} ({e.Message})"; return false; }
        }

        private static bool WriteEnum(SerializedProperty p, JToken v, out string error)
        {
            error = null;
            if (v.Type == JTokenType.Integer) { p.enumValueIndex = v.Value<int>(); return true; }
            var name = v.Value<string>();
            var idx = Array.IndexOf(p.enumNames, name);
            if (idx < 0) { error = $"TYPE_MISMATCH: '{name}' is not a member of the enum"; return false; }
            p.enumValueIndex = idx; return true;
        }

        private static bool WriteRef(SerializedProperty p, JToken v, out string error)
        {
            error = null;
            if (v == null || v.Type == JTokenType.Null) { p.objectReferenceValue = null; return true; }
            var obj = SerializedTargeting.ResolveObjectReference(v as JObject, out error);
            if (obj == null && error != null) return false;
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
```

(`SerializedTargeting.ResolveObjectReference` is added in Task 2; for Task 1 add a temporary stub returning `null` so this compiles, then flesh it out in Task 2. The Task-1 fixture has no non-null ObjectReference round-trip, so the stub is exercised only by Task 2's tests.)

- [ ] **Step 5: Verify it passes** — recompile → EditMode green for `SerializedValueTests`.
- [ ] **Step 6: Commit** `feat(0.7.0): SerializedValue — canonical JSON read/write per SerializedPropertyType`.

---

## Task 2: Targeting + `inspect_serialized_object` (read, single target)

**Files:** Create `SerializedTargeting.cs`; create `SerializedMemberHandler.cs` (with `Inspect`); add tests to `SerializedMemberHandlerTests.cs`.

- [ ] **Step 1: Write the failing test** (`SerializedMemberHandlerTests.cs`)

```csharp
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

        internal static JToken FindProp(JArray props, string path)
        {
            foreach (var p in props) if ((string)p["propertyPath"] == path) return p;
            return null;
        }
    }
}
```

- [ ] **Step 2: Verify it fails** — `SerializedMemberHandler`/`SerializedTargeting` undefined.

- [ ] **Step 3: Implement `SerializedTargeting.cs`**

```csharp
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    public struct ResolvedTarget { public Object Obj; public Component Component; public string Describe; }

    public static class SerializedTargeting
    {
        // Resolve a single target descriptor (+ optional component/componentIndex siblings) → the Object whose
        // SerializedObject we edit. Returns false + code/message on failure.
        public static bool ResolveSingle(JObject target, JObject parent, out ResolvedTarget result, out string code, out string message)
        {
            result = default; code = null; message = null;
            Object obj = null;
            if (target?["instanceId"] != null) obj = EditorUtility.InstanceIDToObject(target["instanceId"].Value<int>());
            else if (target?["assetPath"] != null) obj = AssetDatabase.LoadMainAssetAtPath(target["assetPath"].Value<string>());
            else if (target?["guid"] != null) obj = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(target["guid"].Value<string>()));
            else if (target?["scenePath"] != null) obj = FindByScenePath(target["scenePath"].Value<string>());
            else { code = "VALIDATION_ERROR"; message = "target needs one of instanceId/assetPath/guid/scenePath"; return false; }

            if (obj == null) { code = "TARGET_NOT_FOUND"; message = "target did not resolve"; return false; }

            // If the resolved object is a GameObject and a component is named, narrow to that component.
            var compName = parent?["component"]?.Value<string>();
            if (obj is GameObject go && compName != null)
            {
                var idx = parent?["componentIndex"]?.Value<int>() ?? 0;
                var comp = go.GetComponents<Component>().Where(c => c != null && c.GetType().Name == compName).Skip(idx).FirstOrDefault();
                if (comp == null) { code = "COMPONENT_NOT_FOUND"; message = $"component {compName}[{idx}] not found"; return false; }
                result = new ResolvedTarget { Obj = comp, Component = comp, Describe = $"{ScenePath(go)}::{compName}[{idx}]" };
                return true;
            }
            result = new ResolvedTarget { Obj = obj, Component = obj as Component, Describe = AssetDatabase.GetAssetPath(obj) is var ap && !string.IsNullOrEmpty(ap) ? ap : obj.name };
            return true;
        }

        public static List<ResolvedTarget> ResolveMatch(JObject match, JObject parent, int max, out string code, out string message)
        {
            code = null; message = null;
            var list = new List<ResolvedTarget>();
            IEnumerable<GameObject> roots = LoadedRootObjects();
            IEnumerable<GameObject> gos;
            if (match?["scenePaths"] is JArray paths) gos = paths.Select(p => FindByScenePath(p.Value<string>())).Where(g => g != null).Cast<GameObject>();
            else if (match?["selection"]?.Value<bool>() == true) gos = Selection.gameObjects;
            else if (match?["tag"] != null) gos = AllGameObjects(roots).Where(g => g.CompareTag(match["tag"].Value<string>()));
            else if (match?["componentType"] != null) gos = AllGameObjects(roots).Where(g => g.GetComponents<Component>().Any(c => c != null && c.GetType().Name == match["componentType"].Value<string>()));
            else if (match?["prefab"] != null) { var src = LoadPrefab(match["prefab"].Value<string>()); gos = AllGameObjects(roots).Where(g => PrefabUtility.GetCorrespondingObjectFromSource(g) is GameObject s && (src == null || s == src || PrefabUtility.GetCorrespondingObjectFromSource(s) == src)); }
            else { code = "VALIDATION_ERROR"; message = "match needs one of prefab/componentType/tag/selection/scenePaths"; return list; }

            foreach (var go in gos.Distinct())
            {
                if (list.Count >= max) break;
                if (ResolveSingle(new JObject { ["instanceId"] = go.GetInstanceID() }, parent, out var rt, out _, out _)) list.Add(rt);
            }
            return list;
        }

        public static Object ResolveObjectReference(JObject v, out string error)
        {
            error = null;
            if (v == null) return null;
            if (v["instanceId"] != null) return EditorUtility.InstanceIDToObject(v["instanceId"].Value<int>());
            if (v["guid"] != null) return AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(v["guid"].Value<string>()));
            if (v["assetPath"] != null) return AssetDatabase.LoadMainAssetAtPath(v["assetPath"].Value<string>());
            if (v["scenePath"] != null) return FindByScenePath(v["scenePath"].Value<string>());
            error = "TYPE_MISMATCH: object reference needs instanceId/guid/assetPath/scenePath or null";
            return null;
        }

        private static GameObject LoadPrefab(string s) => s != null && s.StartsWith("Assets/") ? AssetDatabase.LoadAssetAtPath<GameObject>(s) : AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(s));
        private static IEnumerable<GameObject> LoadedRootObjects() { for (int i = 0; i < SceneManager.sceneCount; i++) { var sc = SceneManager.GetSceneAt(i); if (sc.isLoaded) foreach (var r in sc.GetRootGameObjects()) yield return r; } }
        private static IEnumerable<GameObject> AllGameObjects(IEnumerable<GameObject> roots) { foreach (var r in roots) foreach (var t in r.GetComponentsInChildren<Transform>(true)) yield return t.gameObject; }
        private static GameObject FindByScenePath(string path) => AllGameObjects(LoadedRootObjects()).FirstOrDefault(g => ScenePath(g) == path);
        public static string ScenePath(GameObject go) { var s = "/" + go.name; var t = go.transform; while (t.parent != null) { t = t.parent; s = "/" + t.name + s; } return s; }
    }
}
```

- [ ] **Step 4: Implement `SerializedMemberHandler.Inspect`** (start the handler file)

```csharp
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    public static partial class SerializedMemberHandler
    {
        private const int MaxObjectsCeiling = 500;

        public static HandlerOutcome Inspect(JObject p)
        {
            try
            {
                var depth = p["depth"]?.ToObject<int?>() ?? 3;
                var includeValues = p["includeValues"]?.ToObject<bool?>() ?? true;
                var pathPrefix = p["pathPrefix"]?.ToString();

                if (p["match"] is JObject match)
                {
                    var max = Mathf.Clamp(p["maxObjects"]?.ToObject<int?>() ?? 50, 1, MaxObjectsCeiling);
                    var targets = SerializedTargeting.ResolveMatch(match, p, max + 1, out var code, out var msg);
                    if (code != null) return HandlerOutcome.Fail(msg, code);
                    var truncated = targets.Count > max;
                    var arr = new JArray();
                    foreach (var t in targets.GetRange(0, Mathf.Min(targets.Count, max)))
                        arr.Add(new JObject { ["target"] = t.Describe, ["properties"] = Tree(new SerializedObject(t.Obj), pathPrefix, depth, includeValues) });
                    return HandlerOutcome.Ok(new JObject { ["count"] = arr.Count, ["truncated"] = truncated, ["objects"] = arr });
                }

                if (!SerializedTargeting.ResolveSingle(p["target"] as JObject, p, out var rt, out var c2, out var m2)) return HandlerOutcome.Fail(m2, c2);
                var so = new SerializedObject(rt.Obj);
                return HandlerOutcome.Ok(new JObject {
                    ["target"] = rt.Describe,
                    ["object"] = new JObject { ["type"] = rt.Obj.GetType().Name, ["properties"] = Tree(so, pathPrefix, depth, includeValues) } });
            }
            catch (System.Exception e) { return HandlerOutcome.Fail($"inspect failed: {e.Message}"); }
        }

        // Walk visible serialized properties to a depth, optionally scoped to a path prefix.
        private static JArray Tree(SerializedObject so, string prefix, int maxDepth, bool includeValues)
        {
            var arr = new JArray();
            var it = string.IsNullOrEmpty(prefix) ? so.GetIterator() : so.FindProperty(prefix);
            if (it == null) return arr;
            bool enterChildren = true;
            var startDepth = it.depth;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = it.depth - startDepth < maxDepth && it.hasVisibleChildren;
                if (it.propertyPath == "m_Script") continue;
                var node = new JObject { ["propertyPath"] = it.propertyPath, ["propertyType"] = it.propertyType.ToString() };
                if (it.isArray && it.propertyType != SerializedPropertyType.String) node["arraySize"] = it.arraySize;
                if (it.propertyType == SerializedPropertyType.ManagedReference) node["managedReferenceFullTypename"] = it.managedReferenceFullTypename;
                if (includeValues && !it.hasVisibleChildren) node["value"] = SerializedValue.Read(it);
                arr.Add(node);
            }
            return arr;
        }
    }
}
```

- [ ] **Step 5: Verify it passes** — EditMode green for `Inspect_AssetByPath…`. Also replace the Task-1 `ResolveObjectReference` stub with the real one (now present).
- [ ] **Step 6: Commit** `feat(0.7.0): SerializedTargeting + inspect_serialized_object (introspection)`.

---

## Task 3: `set_serialized_properties` Mode 1 (explicit targets, CAS) + correctness

**Files:** add `Set` (Mode 1 path) to `SerializedMemberHandler.cs`; add tests.

- [ ] **Step 1: Write the failing tests** (incl. the headline)

```csharp
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
```

- [ ] **Step 2: Verify it fails** — `Set` undefined.

- [ ] **Step 3: Implement `SerializedMemberHandler.Set` (Mode 1)**

```csharp
public static HandlerOutcome Set(JObject p)
{
    try
    {
        var force = p["force"]?.ToObject<bool?>() ?? false;
        var dryRun = p["dryRun"]?.ToObject<bool?>() ?? false;
        var allOrNothing = p["allOrNothing"]?.ToObject<bool?>() ?? false;
        var withoutUndo = p["withoutUndo"]?.ToObject<bool?>() ?? false;
        var undoLabel = p["undoLabel"]?.ToString() ?? "MCP Set Serialized Properties";

        if (p["match"] is JObject) return SetBySelector(p, force, dryRun, withoutUndo, undoLabel); // Task 4

        if (!(p["edits"] is JArray edits)) return HandlerOutcome.Fail("provide edits[] or match", "VALIDATION_ERROR");

        var changed = new JArray();
        var skipped = new JArray();
        var planned = new List<System.Action>();      // deferred writes (so allOrNothing can abort before any)
        var dirtyObjects = new List<Object>();

        foreach (var edit in edits.OfType<JObject>())
        {
            if (!SerializedTargeting.ResolveSingle(edit["target"] as JObject, edit, out var rt, out var code, out var msg))
            { skipped.Add(Skip(null, null, code, msg)); if (allOrNothing) return Abort(skipped); continue; }
            if (PlayModeBlocks(rt.Obj)) { skipped.Add(Skip(rt.Describe, null, "PLAY_MODE", "scene writes refuse in play mode")); if (allOrNothing) return Abort(skipped); continue; }

            var so = new SerializedObject(rt.Obj);
            foreach (var prop in (edit["set"] as JObject).Properties())
            {
                var path = prop.Name;
                var spec = prop.Value as JObject;
                var sp = so.FindProperty(path);
                if (sp == null) { skipped.Add(Skip(rt.Describe, path, "PROPERTY_NOT_FOUND", "no such property")); if (allOrNothing) return Abort(skipped); continue; }

                var hasExpected = spec["expected"] != null;
                if (!hasExpected && !force) { skipped.Add(Skip(rt.Describe, path, "MISSING_PRECONDITION", "expected required (or force)")); if (allOrNothing) return Abort(skipped); continue; }
                var current = SerializedValue.Read(sp);
                if (hasExpected && !JToken.DeepEquals(current, spec["expected"]))
                { skipped.Add(Skip(rt.Describe, path, "STALE", "value changed", current, spec["expected"])); if (allOrNothing) return Abort(skipped); continue; }

                // validate the write into a throwaway SO first (so allOrNothing is truly all-or-nothing)
                var probe = new SerializedObject(rt.Obj);
                if (!SerializedValue.Write(probe.FindProperty(path), spec["value"], out var werr))
                { skipped.Add(Skip(rt.Describe, path, "TYPE_MISMATCH", werr)); if (allOrNothing) return Abort(skipped); continue; }

                var to = spec["value"];
                changed.Add(new JObject { ["target"] = rt.Describe, ["propertyPath"] = path, ["from"] = current, ["to"] = to });
                var capturedObj = rt.Obj; var capturedPath = path; var capturedVal = to;
                planned.Add(() => ApplyOne(capturedObj, capturedPath, capturedVal, withoutUndo, undoLabel, dirtyObjects));
            }
        }

        if (!dryRun)
        {
            if (!withoutUndo) Undo.IncrementCurrentGroup();
            foreach (var a in planned) a();
            if (!withoutUndo) { Undo.SetCurrentGroupName(undoLabel); Undo.CollapseUndoOperations(Undo.GetCurrentGroup()); }
            foreach (var o in dirtyObjects.Distinct()) EditorUtility.SetDirty(o);
        }
        return HandlerOutcome.Ok(new JObject { ["applied"] = !dryRun, ["changed"] = changed, ["skipped"] = skipped });
    }
    catch (System.Exception e) { return HandlerOutcome.Fail($"set failed: {e.Message}"); }
}

private static void ApplyOne(Object obj, string path, JToken value, bool withoutUndo, string undoLabel, List<Object> dirty)
{
    if (!withoutUndo) Undo.RecordObject(obj, undoLabel);
    var so = new SerializedObject(obj);
    SerializedValue.Write(so.FindProperty(path), value, out _);
    if (withoutUndo) so.ApplyModifiedPropertiesWithoutUndo(); else so.ApplyModifiedProperties();
    if (PrefabUtility.IsPartOfPrefabInstance(obj)) PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
    dirty.Add(obj);
}

private static bool PlayModeBlocks(Object o) => EditorApplication.isPlaying && !(o is ScriptableObject) && AssetDatabase.GetAssetPath(o) == "";
private static JObject Skip(string target, string path, string code, string msg, JToken actual = null, JToken expected = null)
{ var o = new JObject { ["target"] = target, ["propertyPath"] = path, ["code"] = code, ["message"] = msg }; if (actual != null) o["actual"] = actual; if (expected != null) o["expected"] = expected; return o; }
private static HandlerOutcome Abort(JArray skipped) => HandlerOutcome.Fail($"aborted (allOrNothing): {skipped.Count} precondition failure(s)", ((JObject)skipped.Last)["code"].ToString());
```

(Add `using System.Linq;` and `using System.Collections.Generic;` to the handler file.)

- [ ] **Step 4: Verify it passes** — EditMode green incl. the headline + CAS tests.
- [ ] **Step 5: Commit** `feat(0.7.0): set_serialized_properties Mode 1 — CAS + Inspector-correct write (private fields)`.

---

## Task 4: Selector write Mode 2 (preview-token) + prefab-instance test

**Files:** add `SetBySelector` + the token helper to `SerializedMemberHandler.cs`; add tests (selector preview→commit, stale token, force; a prefab-instance override test).

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void SetBySelector_PreviewThenCommit_AppliesToAllMatched()
{
    // two GameObjects each with SerFixtureComponent
    var a = new GameObject("A", typeof(SerFixtureComponent));
    var b = new GameObject("B", typeof(SerFixtureComponent));
    try
    {
        var match = new JObject { ["match"] = new JObject { ["componentType"] = "SerFixtureComponent" },
                                   ["component"] = "SerFixtureComponent",
                                   ["set"] = new JObject { ["Hp"] = 250 } };
        var preview = JObject.FromObject(SerializedMemberHandler.Set((JObject)match.DeepClone()).Payload);
        Assert.IsFalse((bool)preview["applied"]);
        Assert.IsNotNull(preview["token"]);
        Assert.AreEqual(2, ((JArray)preview["objects"]).Count);

        var commit = (JObject)match.DeepClone(); commit["token"] = preview["token"];
        var done = SerializedMemberHandler.Set(commit);
        Assert.IsFalse(done.IsError, done.Error);
        Assert.AreEqual(250, a.GetComponent<SerFixtureComponent>().Hp);
        Assert.AreEqual(250, b.GetComponent<SerFixtureComponent>().Hp);
    }
    finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
}

[Test] public void SetBySelector_StaleToken_Rejected()
{
    var a = new GameObject("A", typeof(SerFixtureComponent));
    try
    {
        var match = new JObject { ["match"] = new JObject { ["componentType"] = "SerFixtureComponent" }, ["component"] = "SerFixtureComponent", ["set"] = new JObject { ["Hp"] = 9 } };
        var preview = JObject.FromObject(SerializedMemberHandler.Set((JObject)match.DeepClone()).Payload);
        a.GetComponent<SerFixtureComponent>().Hp = 123; // state changes after preview
        var commit = (JObject)match.DeepClone(); commit["token"] = preview["token"];
        var done = SerializedMemberHandler.Set(commit);
        Assert.IsTrue(done.IsError);
        Assert.AreEqual("STALE_MATCH", done.Code);
    }
    finally { Object.DestroyImmediate(a); }
}
```

- [ ] **Step 2: Verify it fails.**

- [ ] **Step 3: Implement `SetBySelector` + the stateless token**

```csharp
private static HandlerOutcome SetBySelector(JObject p, bool force, bool dryRun, bool withoutUndo, string undoLabel)
{
    var match = (JObject)p["match"];
    var set = p["set"] as JObject;
    if (set == null) return HandlerOutcome.Fail("match write needs set{}", "VALIDATION_ERROR");
    var paths = set.Properties().Select(x => x.Name).ToList();
    var targets = SerializedTargeting.ResolveMatch(match, p, MaxObjectsCeiling, out var code, out var msg);
    if (code != null) return HandlerOutcome.Fail(msg, code);

    var liveToken = MatchToken(targets, paths);
    var providedToken = p["token"]?.ToString();

    if (providedToken == null && !force)
    {
        // preview: matched set + current values for the touched paths + the token (no mutation)
        var objects = new JArray();
        foreach (var t in targets)
        {
            var so = new SerializedObject(t.Obj); var cur = new JObject();
            foreach (var path in paths) { var sp = so.FindProperty(path); cur[path] = sp != null ? SerializedValue.Read(sp) : JValue.CreateNull(); }
            objects.Add(new JObject { ["target"] = t.Describe, ["current"] = cur });
        }
        return HandlerOutcome.Ok(new JObject { ["applied"] = false, ["count"] = targets.Count, ["objects"] = objects, ["token"] = liveToken });
    }

    if (!force && providedToken != liveToken) return HandlerOutcome.Fail("matched set or values changed since preview", "STALE_MATCH");

    var changed = new JArray();
    if (!dryRun && !withoutUndo) Undo.IncrementCurrentGroup();
    var dirty = new List<Object>();
    foreach (var t in targets)
        foreach (var path in paths)
        {
            var probe = new SerializedObject(t.Obj); var sp = probe.FindProperty(path);
            if (sp == null) continue;
            var from = SerializedValue.Read(sp);
            if (!dryRun) ApplyOne(t.Obj, path, set[path], withoutUndo, undoLabel, dirty);
            changed.Add(new JObject { ["target"] = t.Describe, ["propertyPath"] = path, ["from"] = from, ["to"] = set[path] });
        }
    if (!dryRun)
    {
        if (!withoutUndo) { Undo.SetCurrentGroupName(undoLabel); Undo.CollapseUndoOperations(Undo.GetCurrentGroup()); }
        foreach (var o in dirty.Distinct()) EditorUtility.SetDirty(o);
    }
    return HandlerOutcome.Ok(new JObject { ["applied"] = !dryRun, ["forced"] = force, ["changed"] = changed });
}

// Stateless token: SHA-256 over (sorted instanceId + current canonical value at each touched path).
private static string MatchToken(List<ResolvedTarget> targets, List<string> paths)
{
    var sb = new System.Text.StringBuilder();
    foreach (var t in targets.OrderBy(x => x.Obj.GetInstanceID()))
    {
        sb.Append(t.Obj.GetInstanceID()).Append('|');
        var so = new SerializedObject(t.Obj);
        foreach (var path in paths) { var sp = so.FindProperty(path); sb.Append(path).Append('=').Append(sp != null ? SerializedValue.Read(sp).ToString(Newtonsoft.Json.Formatting.None) : "∅").Append(';'); }
        sb.Append('\n');
    }
    using (var sha = System.Security.Cryptography.SHA256.Create())
        return System.BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").ToLowerInvariant();
}
```

- [ ] **Step 4: Verify it passes.**
- [ ] **Step 5: Commit** `feat(0.7.0): set_serialized_properties Mode 2 — selector preview-token (STALE_MATCH) + force`.

---

## Task 5: `save_assets`, catalog, registration, and floor dogfood

**Files:** add `SaveAssets` to the handler; modify `UnityEditorMCP.cs`, `protocol/catalog/commands.json`; regen.

- [ ] **Step 1: Implement `SaveAssets`**

```csharp
public static HandlerOutcome SaveAssets(JObject p)
{
    try
    {
        if (p["targets"] is JArray targets)
        {
            int n = 0;
            foreach (var t in targets.OfType<JObject>())
                if (SerializedTargeting.ResolveSingle(t, t, out var rt, out _, out _) && AssetDatabase.Contains(rt.Obj)) { AssetDatabase.SaveAssetIfDirty(rt.Obj); n++; }
            return HandlerOutcome.Ok(new JObject { ["saved"] = n });
        }
        AssetDatabase.SaveAssets();
        return HandlerOutcome.Ok(new JObject { ["saved"] = "all-dirty" });
    }
    catch (System.Exception e) { return HandlerOutcome.Fail($"save failed: {e.Message}"); }
}
```

(`AssetDatabase.SaveAssetIfDirty(Object)` is 2020.3+ — on the floor. Guard with `#if UNITY_2020_3_OR_NEWER` / fall back to `SaveAssets()` if COMPATIBILITY.md ever lowers the floor.)

- [ ] **Step 2: Register** — in `BuildDispatcher` (`UnityEditorMCP.cs`):
```
dispatcher.Register("inspect_serialized_object", SerializedMemberHandler.Inspect);
dispatcher.Register("set_serialized_properties", SerializedMemberHandler.Set);
dispatcher.Register("save_assets", SerializedMemberHandler.SaveAssets);
```

- [ ] **Step 3: Catalog** — add the three entries to `protocol/catalog/commands.json` (`sides:["editor"]`, category `serialization`, full `params`/`result` schemas per spec §4). Then `node protocol/scripts/generate-csharp-catalog.mjs` and `node protocol/scripts/check-drift.mjs` (expect OK).

- [ ] **Step 4: Floor verification (dogfood)** — `compat-lint`; recompile on 2020.3; EditMode all green; then live-smoke on a real object:
  - `call_unity_tool(7093, inspect_serialized_object, {target:{...}})` → tree returned;
  - `set_serialized_properties` Mode 1 with a stale `expected` → `STALE`; with the right `expected` → applied;
  - `save_assets` persists.

- [ ] **Step 5: Commit** `feat(0.7.0): save_assets + catalog + dispatcher wiring; floor-verified (Serialization Core)`.

---

## Self-review

- **Spec coverage:** D1 (`SerializedObject` pipeline), D2 (target union + selector), D3 (`propertyPath` incl. `Array.data[i]` via the iterator), D4 (`inspect` tree), D5 (the value matrix; exotic types read-only as specced), D6 (private `[SerializeField]` — Task 3 headline test), D9 (Undo group, `ApplyModifiedProperties`(+`WithoutUndo`), `SetDirty`, `RecordPrefabInstancePropertyModifications`, separate `save_assets`); the safety model §6 (Mode 1 CAS, Mode 2 token, `force`); error model §10; play-mode guard. Deferred items (D7/D8 write, curve/gradient write, E) are not implemented — matches spec §11.
- **Type consistency:** `SerializedValue.Read/Write(SerializedProperty, JToken)`; `SerializedTargeting.ResolveSingle(JObject target, JObject parent, out ResolvedTarget, out code, out msg)` / `ResolveMatch(...)→List<ResolvedTarget>` / `ResolveObjectReference(JObject)→Object`; `ResolvedTarget{Obj,Component,Describe}`; `ApplyOne(...)` and `MatchToken(...)` signatures match every call site. The Task-1 `ResolveObjectReference` stub is replaced in Task 2 (noted).
- **Floor-safety:** typed accessors only (no `boxedValue`); `SaveAssetIfDirty` flagged for a guard; `managedReferenceFullTypename`/`animationCurveValue` are read-only and floor-available. Add any guarded site to COMPATIBILITY.md.
- **No placeholders:** every task has the fixture/test/impl and the exact bridge verification. The catalog JSON schemas (Task 5 Step 3) are the one place the executor authors schema text from spec §4 — the field set is fully enumerated there.
- **Assumption to verify at execution:** `SerializedObject.GetIterator().NextVisible` depth bounding in `Tree()` — confirm the depth math on a nested fixture; if Unity's `m_Script` filtering differs, the `m_Script` skip already guards the common case.
