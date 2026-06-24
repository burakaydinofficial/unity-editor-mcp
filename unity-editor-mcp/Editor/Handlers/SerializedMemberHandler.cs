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
            // A pathPrefix pointing at a leaf property (no visible children — e.g. m_Name, m_Intensity,
            // m_ConnectedBody) emits that property itself; the subtree-scoped walk below would otherwise step
            // straight to a sibling and return nothing.
            if (!string.IsNullOrEmpty(prefix) && !it.hasVisibleChildren)
            {
                var leaf = new JObject { ["propertyPath"] = it.propertyPath, ["propertyType"] = it.propertyType.ToString() };
                if (it.isArray && it.propertyType != SerializedPropertyType.String) leaf["arraySize"] = it.arraySize;
                if (it.propertyType == SerializedPropertyType.ManagedReference) { leaf["managedReferenceFullTypename"] = it.managedReferenceFullTypename; leaf["managedReferenceFieldTypename"] = it.managedReferenceFieldTypename; }
                if (includeValues) leaf["value"] = SerializedValue.Read(it);
                arr.Add(leaf);
                return arr;
            }
            bool enterChildren = true;
            var startDepth = it.depth;
            bool scoped = !string.IsNullOrEmpty(prefix); // with a prefix, emit only its subtree (no sibling leak)
            while (it.NextVisible(enterChildren))
            {
                if (scoped && it.depth <= startDepth) break; // left the prefix's subtree — stop
                if (it.propertyPath == "m_Script") { enterChildren = false; continue; }
                // Struct types (Vector/Quaternion/Color/Rect/Bounds) are read/written as a UNIT by SerializedValue —
                // emit their composite value and do NOT recurse into x/y/z, so a read round-trips into a write.
                var atomic = IsAtomicComposite(it.propertyType);
                enterChildren = !atomic && it.depth - startDepth < maxDepth && it.hasVisibleChildren;
                var node = new JObject { ["propertyPath"] = it.propertyPath, ["propertyType"] = it.propertyType.ToString() };
                if (it.isArray && it.propertyType != SerializedPropertyType.String) node["arraySize"] = it.arraySize;
                if (it.propertyType == SerializedPropertyType.ManagedReference) { node["managedReferenceFullTypename"] = it.managedReferenceFullTypename; node["managedReferenceFieldTypename"] = it.managedReferenceFieldTypename; }
                if (includeValues && (atomic || !it.hasVisibleChildren)) node["value"] = SerializedValue.Read(it);
                arr.Add(node);
            }
            return arr;
        }

        // Value-equality for compare-and-swap: numbers compare NUMERICALLY (a read emits floats, e.g. -10.0,
        // but an agent echoing -10 sends a JSON integer — JToken.DeepEquals would call them unequal). Objects
        // and arrays recurse so {x:0,y:0,z:-10} (floats) matches {x:0,y:0,z:-10} (ints).
        private static bool ValuesEqual(JToken a, JToken b)
        {
            if (a == null || b == null) return (a == null) == (b == null);
            bool aNum = a.Type == JTokenType.Integer || a.Type == JTokenType.Float;
            bool bNum = b.Type == JTokenType.Integer || b.Type == JTokenType.Float;
            if (aNum && bNum) { double av = a.Value<double>(), bv = b.Value<double>(); return av == bv || (double.IsNaN(av) && double.IsNaN(bv)); }
            if (a.Type == JTokenType.Object && b.Type == JTokenType.Object)
            {
                var ao = (JObject)a; var bo = (JObject)b;
                if (ao.Count != bo.Count) return false;
                foreach (var prop in ao) { if (!bo.TryGetValue(prop.Key, out var bv) || !ValuesEqual(prop.Value, bv)) return false; }
                return true;
            }
            if (a.Type == JTokenType.Array && b.Type == JTokenType.Array)
            {
                var aa = (JArray)a; var ba = (JArray)b;
                if (aa.Count != ba.Count) return false;
                for (int i = 0; i < aa.Count; i++) if (!ValuesEqual(aa[i], ba[i])) return false;
                return true;
            }
            return JToken.DeepEquals(a, b);
        }

        private static bool IsAtomicComposite(SerializedPropertyType t)
        {
            switch (t)
            {
                case SerializedPropertyType.Vector2:
                case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Vector2Int:
                case SerializedPropertyType.Vector3Int:
                case SerializedPropertyType.Quaternion:
                case SerializedPropertyType.Color:
                case SerializedPropertyType.Rect:
                case SerializedPropertyType.Bounds:
                    return true;
                default:
                    return false;
            }
        }

        public static HandlerOutcome Set(JObject p)
        {
            try
            {
                var force = p["force"]?.ToObject<bool?>() ?? false;
                var dryRun = p["dryRun"]?.ToObject<bool?>() ?? false;
                var allOrNothing = p["allOrNothing"]?.ToObject<bool?>() ?? false;
                var withoutUndo = p["withoutUndo"]?.ToObject<bool?>() ?? false;
                var undoLabel = p["undoLabel"]?.ToString() ?? "MCP Set Serialized Properties";

                if (p["match"] is JObject) return SetBySelector(p, force, dryRun, allOrNothing, withoutUndo, undoLabel);

                if (!(p["edits"] is JArray edits)) return HandlerOutcome.Fail("provide edits[] or match", "VALIDATION_ERROR");

                var changed = new JArray();
                var skipped = new JArray();
                var planned = new List<System.Action>();
                var dirtyObjects = new List<Object>();

                foreach (var edit in edits.OfType<JObject>())
                {
                    if (!SerializedTargeting.ResolveSingle(edit["target"] as JObject, edit, out var rt, out var code, out var msg))
                    { skipped.Add(Skip(null, null, code, msg)); if (allOrNothing) return Abort(skipped); continue; }
                    if (PlayModeBlocks(rt.Obj)) { skipped.Add(Skip(rt.Describe, null, "PLAY_MODE", "scene writes refuse in play mode")); if (allOrNothing) return Abort(skipped); continue; }

                    var so = new SerializedObject(rt.Obj);
                    foreach (var prop in ((JObject)edit["set"]).Properties())
                    {
                        var path = prop.Name;
                        var spec = prop.Value as JObject;
                        if (spec == null) { skipped.Add(Skip(rt.Describe, path, "VALIDATION_ERROR", "set entry must be an object {value, expected}")); if (allOrNothing) return Abort(skipped); continue; }
                        var sp = so.FindProperty(path);
                        if (sp == null) { skipped.Add(Skip(rt.Describe, path, "PROPERTY_NOT_FOUND", "no such property")); if (allOrNothing) return Abort(skipped); continue; }

                        var hasExpected = spec["expected"] != null;
                        if (!hasExpected && !force) { skipped.Add(Skip(rt.Describe, path, "MISSING_PRECONDITION", "expected required (or force)")); if (allOrNothing) return Abort(skipped); continue; }
                        var current = SerializedValue.Read(sp);
                        // round-7 BUG 2: `force` must skip the compare-and-swap even when `expected` is supplied
                        // (schema: "force skips expected"). Previously force only made `expected` optional.
                        if (hasExpected && !force && !ValuesEqual(current, spec["expected"]))
                        { skipped.Add(Skip(rt.Describe, path, "STALE", "value changed", current, spec["expected"])); if (allOrNothing) return Abort(skipped); continue; }

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
                    try { foreach (var a in planned) a(); }
                    finally { if (!withoutUndo) { Undo.SetCurrentGroupName(undoLabel); Undo.CollapseUndoOperations(Undo.GetCurrentGroup()); } }
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

        // Mode 2: selector write — preview (no token → matched set + current values + token, no mutation),
        // then commit with the token (re-verified against live state; STALE_MATCH if it drifted). force skips.
        private static HandlerOutcome SetBySelector(JObject p, bool force, bool dryRun, bool allOrNothing, bool withoutUndo, string undoLabel)
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

            // Play-mode guard (spec §9): scene-object writes refuse in play mode — even under force.
            foreach (var t in targets)
                if (PlayModeBlocks(t.Obj)) return HandlerOutcome.Fail("scene writes refuse in play mode", "PLAY_MODE");

            var changed = new JArray();
            var skipped = new JArray();
            var plan = new List<(Object obj, string path, JToken value)>();
            // Validate every write up front (probe) so a TYPE_MISMATCH is surfaced (not silently dropped) and
            // allOrNothing is honored before any mutation.
            foreach (var t in targets)
                foreach (var path in paths)
                {
                    var probe = new SerializedObject(t.Obj); var sp = probe.FindProperty(path);
                    if (sp == null) { skipped.Add(Skip(t.Describe, path, "PROPERTY_NOT_FOUND", "no such property")); if (allOrNothing) return Abort(skipped); continue; }
                    if (!SerializedValue.Write(sp, set[path], out var werr)) { skipped.Add(Skip(t.Describe, path, "TYPE_MISMATCH", werr)); if (allOrNothing) return Abort(skipped); continue; }
                    changed.Add(new JObject { ["target"] = t.Describe, ["propertyPath"] = path, ["from"] = SerializedValue.Read(new SerializedObject(t.Obj).FindProperty(path)), ["to"] = set[path] });
                    plan.Add((t.Obj, path, set[path]));
                }

            if (!dryRun)
            {
                var dirty = new List<Object>();
                if (!withoutUndo) Undo.IncrementCurrentGroup();
                try { foreach (var (obj, path, value) in plan) ApplyOne(obj, path, value, withoutUndo, undoLabel, dirty); }
                finally { if (!withoutUndo) { Undo.SetCurrentGroupName(undoLabel); Undo.CollapseUndoOperations(Undo.GetCurrentGroup()); } }
                foreach (var o in dirty.Distinct()) EditorUtility.SetDirty(o);
            }
            return HandlerOutcome.Ok(new JObject { ["applied"] = !dryRun, ["forced"] = force, ["changed"] = changed, ["skipped"] = skipped });
        }

        // Stateless token: SHA-256 over (sorted instanceId + current canonical value at each touched path).
        private static string MatchToken(List<ResolvedTarget> targets, List<string> paths)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in targets.OrderBy(x => x.Obj.GetInstanceID()))
            {
                sb.Append(t.Obj.GetInstanceID()).Append('|');
                var so = new SerializedObject(t.Obj);
                foreach (var path in paths) { var sp = so.FindProperty(path); sb.Append(path).Append('=').Append(sp != null ? SerializedValue.Read(sp).ToString(Newtonsoft.Json.Formatting.None) : "(null)").Append(';'); }
                sb.Append('\n');
            }
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return System.BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        // Persist dirty assets. set_serialized_properties writes are dirty-only — this is the explicit save.
        // SaveAssets() (not the 2020.3-patch SaveAssetIfDirty) keeps the floor claim safe; it persists exactly
        // what the writes dirtied.
        public static HandlerOutcome SaveAssets(JObject p)
        {
            try
            {
                AssetDatabase.SaveAssets();
                return HandlerOutcome.Ok(new JObject { ["saved"] = "all-dirty" });
            }
            catch (System.Exception e) { return HandlerOutcome.Fail($"save failed: {e.Message}"); }
        }

        // Structural array mutation (D8): a batch of ops over array properties, with size compare-and-swap
        // (expectedSize) mirroring the value CAS. Reuses targeting + the play-mode guard + the Undo/dirty rail.
        public static HandlerOutcome ModifyArray(JObject p)
        {
            try
            {
                var force = p["force"]?.ToObject<bool?>() ?? false;
                var dryRun = p["dryRun"]?.ToObject<bool?>() ?? false;
                var allOrNothing = p["allOrNothing"]?.ToObject<bool?>() ?? false;
                var withoutUndo = p["withoutUndo"]?.ToObject<bool?>() ?? false;
                var undoLabel = p["undoLabel"]?.ToString() ?? "MCP Modify Serialized Array";
                if (!(p["ops"] is JArray ops)) return HandlerOutcome.Fail("provide ops[]", "VALIDATION_ERROR");

                var changed = new JArray();
                var skipped = new JArray();
                var plan = new List<System.Action>();
                var dirty = new List<Object>();
                var seenArrays = new HashSet<string>();

                foreach (var op in ops.OfType<JObject>())
                {
                    if (!SerializedTargeting.ResolveSingle(op["target"] as JObject, op, out var rt, out var code, out var msg))
                    { skipped.Add(Skip(null, null, code, msg)); if (allOrNothing) return Abort(skipped); continue; }
                    if (PlayModeBlocks(rt.Obj)) { skipped.Add(Skip(rt.Describe, null, "PLAY_MODE", "scene writes refuse in play mode")); if (allOrNothing) return Abort(skipped); continue; }

                    var arrayPath = op["arrayPath"]?.ToString();
                    var opName = op["op"]?.ToString();
                    var sp = new SerializedObject(rt.Obj).FindProperty(arrayPath);
                    if (sp == null) { skipped.Add(Skip(rt.Describe, arrayPath, "PROPERTY_NOT_FOUND", "no such property")); if (allOrNothing) return Abort(skipped); continue; }
                    if (!sp.isArray || sp.propertyType == SerializedPropertyType.String) { skipped.Add(Skip(rt.Describe, arrayPath, "NOT_AN_ARRAY", "property is not an array")); if (allOrNothing) return Abort(skipped); continue; }
                    // Reject 2+ ops on the SAME array in one batch: ops apply sequentially, so a later op's index
                    // (validated against the pre-batch size) would land on a shifted element — silent corruption.
                    // Each call is its own Undo group, so this is no loss: send separate calls.
                    if (!seenArrays.Add(rt.Obj.GetInstanceID() + ":" + arrayPath)) { skipped.Add(Skip(rt.Describe, arrayPath, "VALIDATION_ERROR", "multiple ops on the same array in one batch are unsupported — send separate calls")); if (allOrNothing) return Abort(skipped); continue; }

                    var size = sp.arraySize;
                    var hasExpected = op["expectedSize"] != null;
                    if (!hasExpected && !force) { skipped.Add(Skip(rt.Describe, arrayPath, "MISSING_PRECONDITION", "expectedSize required (or force)")); if (allOrNothing) return Abort(skipped); continue; }
                    if (hasExpected && !force && op["expectedSize"].Value<int>() != size) { skipped.Add(Skip(rt.Describe, arrayPath, "STALE_SIZE", "array size changed", size, op["expectedSize"])); if (allOrNothing) return Abort(skipped); continue; } // round-7 BUG 2: force skips the size CAS too

                    if (!ValidateArrayOp(opName, op, size, out var newSize, out var verr, out var vcode))
                    { skipped.Add(Skip(rt.Describe, arrayPath, vcode, verr)); if (allOrNothing) return Abort(skipped); continue; }

                    // Probe an insert value's type up front (no mutation) so a TYPE_MISMATCH is surfaced — not
                    // silently dropped at apply time — and allOrNothing is honored.
                    if (opName == "insert" && op["value"] != null)
                    {
                        var pso = new SerializedObject(rt.Obj); var parr = pso.FindProperty(arrayPath);
                        var iidx = op["index"]?.ToObject<int?>() ?? size;
                        parr.InsertArrayElementAtIndex(iidx);
                        if (!SerializedValue.Write(parr.GetArrayElementAtIndex(iidx), op["value"], out var iverr))
                        { skipped.Add(Skip(rt.Describe, arrayPath, "TYPE_MISMATCH", iverr)); if (allOrNothing) return Abort(skipped); continue; }
                    }

                    changed.Add(new JObject { ["target"] = rt.Describe, ["arrayPath"] = arrayPath, ["op"] = opName, ["fromSize"] = size, ["toSize"] = newSize });
                    var capObj = rt.Obj; var capPath = arrayPath; var capOp = op;
                    plan.Add(() => ApplyArrayOp(capObj, capPath, capOp, withoutUndo, undoLabel, dirty));
                }

                if (!dryRun)
                {
                    if (!withoutUndo) Undo.IncrementCurrentGroup();
                    try { foreach (var a in plan) a(); }
                    finally { if (!withoutUndo) { Undo.SetCurrentGroupName(undoLabel); Undo.CollapseUndoOperations(Undo.GetCurrentGroup()); } }
                    foreach (var o in dirty.Distinct()) EditorUtility.SetDirty(o);
                }
                return HandlerOutcome.Ok(new JObject { ["applied"] = !dryRun, ["changed"] = changed, ["skipped"] = skipped });
            }
            catch (System.Exception e) { return HandlerOutcome.Fail($"modify array failed: {e.Message}"); }
        }

        private static bool ValidateArrayOp(string op, JObject o, int size, out int newSize, out string error, out string code)
        {
            newSize = size; error = null; code = "VALIDATION_ERROR";
            switch (op)
            {
                case "resize":
                    var count = o["count"]?.ToObject<int?>() ?? -1;
                    if (count < 0) { error = "resize needs count >= 0"; return false; }
                    newSize = count; return true;
                case "insert":
                    var ii = o["index"]?.ToObject<int?>() ?? size;
                    if (ii < 0 || ii > size) { code = "INDEX_OUT_OF_RANGE"; error = $"index {ii} out of [0,{size}]"; return false; }
                    newSize = size + 1; return true;
                case "remove":
                    var ri = o["index"]?.ToObject<int?>() ?? -1;
                    if (ri < 0 || ri >= size) { code = "INDEX_OUT_OF_RANGE"; error = $"index {ri} out of [0,{size})"; return false; }
                    newSize = size - 1; return true;
                case "move":
                    int from = o["index"]?.ToObject<int?>() ?? -1, to = o["toIndex"]?.ToObject<int?>() ?? -1;
                    if (from < 0 || from >= size || to < 0 || to >= size) { code = "INDEX_OUT_OF_RANGE"; error = $"move index out of [0,{size})"; return false; }
                    return true;
                case "clear": newSize = 0; return true;
                default: error = $"unknown op '{op}'"; return false;
            }
        }

        private static void ApplyArrayOp(Object obj, string arrayPath, JObject o, bool withoutUndo, string undoLabel, List<Object> dirty)
        {
            if (!withoutUndo) Undo.RecordObject(obj, undoLabel);
            var so = new SerializedObject(obj);
            var sp = so.FindProperty(arrayPath);
            switch (o["op"].ToString())
            {
                case "resize": sp.arraySize = o["count"].Value<int>(); break;
                case "insert":
                    var ii = o["index"]?.ToObject<int?>() ?? sp.arraySize;
                    sp.InsertArrayElementAtIndex(ii);
                    if (o["value"] != null) SerializedValue.Write(sp.GetArrayElementAtIndex(ii), o["value"], out _);
                    break;
                case "remove":
                    var ri = o["index"].Value<int>();
                    var before = sp.arraySize;
                    sp.DeleteArrayElementAtIndex(ri);
                    // object-reference array quirk: a non-null element is NULLED (size unchanged) by the first
                    // delete and needs a second to actually remove. Detect by whether the size dropped — don't
                    // assume (it's version/context-dependent), or a double-delete would remove TWO elements.
                    if (sp.arraySize == before) sp.DeleteArrayElementAtIndex(ri);
                    break;
                case "move": sp.MoveArrayElement(o["index"].Value<int>(), o["toIndex"].Value<int>()); break;
                case "clear": sp.ClearArray(); break;
            }
            if (withoutUndo) so.ApplyModifiedPropertiesWithoutUndo(); else so.ApplyModifiedProperties();
            if (PrefabUtility.IsPartOfPrefabInstance(obj)) PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
            dirty.Add(obj);
        }
    }
}
