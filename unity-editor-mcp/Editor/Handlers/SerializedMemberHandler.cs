using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>Safe SerializedObject-based property editing (ADR 0006 / requirements D). Three commands:
    /// inspect_serialized_object (discovery), set_serialized_properties (the read-before-write batch write),
    /// save_assets (explicit persist). Never C# reflection — private [SerializeField] works by construction.</summary>
    public static class SerializedMemberHandler
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
