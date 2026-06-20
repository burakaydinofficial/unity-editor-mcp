# Serialization — Array Mutation & Curve Writing (0.8.0) Implementation Plan

> REQUIRED SUB-SKILL: superpowers:executing-plans. Spec: `docs/superpowers/specs/2026-06-20-serialization-arrays-curves-design.md`.

**Goal:** Add `AnimationCurve` writing (through the existing `set`) and a new `modify_serialized_array` tool
(resize/insert/remove/move/clear with size-CAS), reusing the 0.7.0 Serialization Core machinery.

**Verification loop:** per task — `refresh_assets` → `read-editor-log.mjs` → `run_tests EditMode` →
`get_test_results`; `compat-lint`; (Task 3) `check-drift`.

---

## Task 1: `AnimationCurve` writing (extend `set`)

**Files:** `SerializedValue.cs`; `Tests/.../SerFixtureAsset.cs` (+ curve field); `SerializedValueTests.cs`.

- [ ] **Step 1: Fixture field** — add to `SerFixtureAsset`: `public AnimationCurve CurveField = AnimationCurve.Linear(0, 0, 1, 1);`
- [ ] **Step 2: Failing test** (`SerializedValueTests`):
```csharp
[Test] public void AnimationCurve_RoundTrips()
{
    var p = _so.FindProperty("CurveField");
    var read = SerializedValue.Read(p);
    Assert.IsTrue(SerializedValue.Write(p, read, out var err), err);
    _so.ApplyModifiedPropertiesWithoutUndo();
    Assert.IsTrue(JToken.DeepEquals(read, SerializedValue.Read(_so.FindProperty("CurveField"))));
    Assert.AreEqual(2, _asset.CurveField.keys.Length);
}
```
- [ ] **Step 3:** In `SerializedValue.Write`, replace the `AnimationCurve` no-case (it falls to the read-only `default`) by adding a case BEFORE `default`:
```csharp
                    case SerializedPropertyType.AnimationCurve:
                    {
                        var keys = v["keys"] as JArray ?? new JArray();
                        var frames = new Keyframe[keys.Count];
                        for (int i = 0; i < keys.Count; i++)
                            frames[i] = new Keyframe(F(keys[i], "time"), F(keys[i], "value"), F(keys[i], "inTangent"), F(keys[i], "outTangent"));
                        p.animationCurveValue = new AnimationCurve(frames);
                        return true;
                    }
```
- [ ] **Step 4:** recompile → EditMode green. **Step 5:** commit `feat(0.8.0): AnimationCurve writing via set`.

---

## Task 2: `modify_serialized_array` (the array ops)

**Files:** `SerializedMemberHandler.cs` (add `ModifyArray` + `ValidateArrayOp` + `ApplyArrayOp`); `SerializedMemberHandlerTests.cs`.

- [ ] **Step 1: Failing tests** (`SerializedMemberHandlerTests`) — over `IntArray = {1,2,3}`:
```csharp
JObject Op(string op, JObject extra) { var o = new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["arrayPath"] = "IntArray", ["op"] = op, ["expectedSize"] = _asset.IntArray.Length }; foreach (var kv in extra) o[kv.Key] = kv.Value; return o; }
HandlerOutcome Arr(JObject op) => SerializedMemberHandler.ModifyArray(new JObject { ["ops"] = new JArray { op } });

[Test] public void Array_Resize() { Assert.IsFalse(Arr(Op("resize", new JObject{["count"]=5})).IsError); Assert.AreEqual(5, _asset.IntArray.Length); }
[Test] public void Array_InsertWithValue() { Assert.IsFalse(Arr(Op("insert", new JObject{["index"]=0,["value"]=99})).IsError); Assert.AreEqual(99, _asset.IntArray[0]); Assert.AreEqual(4, _asset.IntArray.Length); }
[Test] public void Array_Remove() { Assert.IsFalse(Arr(Op("remove", new JObject{["index"]=1})).IsError); Assert.AreEqual(2, _asset.IntArray.Length); Assert.AreEqual(3, _asset.IntArray[1]); }
[Test] public void Array_Move() { Assert.IsFalse(Arr(Op("move", new JObject{["index"]=0,["toIndex"]=2})).IsError); Assert.AreEqual(1, _asset.IntArray[2]); }
[Test] public void Array_Clear() { Assert.IsFalse(Arr(Op("clear", new JObject())).IsError); Assert.AreEqual(0, _asset.IntArray.Length); }
[Test] public void Array_StaleSize_Rejected() { var o = Op("remove", new JObject{["index"]=0}); o["expectedSize"]=99; var r=Arr(o); Assert.AreEqual("STALE_SIZE", (string)((JArray)JObject.FromObject(r.Payload)["skipped"])[0]["code"]); Assert.AreEqual(3,_asset.IntArray.Length); }
[Test] public void Array_MissingExpectedSize_Refused() { var o=Op("clear",new JObject()); o.Remove("expectedSize"); Assert.AreEqual("MISSING_PRECONDITION",(string)((JArray)JObject.FromObject(Arr(o).Payload)["skipped"])[0]["code"]); }
[Test] public void Array_IndexOutOfRange() { Assert.AreEqual("INDEX_OUT_OF_RANGE",(string)((JArray)JObject.FromObject(Arr(Op("remove",new JObject{["index"]=99})).Payload)["skipped"])[0]["code"]); }
[Test] public void Array_NotAnArray() { var o=Op("clear",new JObject()); o["arrayPath"]="IntField"; Assert.AreEqual("NOT_AN_ARRAY",(string)((JArray)JObject.FromObject(Arr(o).Payload)["skipped"])[0]["code"]); }
[Test] public void Array_DryRun_NoMutation() { Assert.IsFalse(SerializedMemberHandler.ModifyArray(new JObject{["dryRun"]=true,["ops"]=new JArray{Op("clear",new JObject())}}).IsError); Assert.AreEqual(3,_asset.IntArray.Length); }
```
- [ ] **Step 2: Verify fail** (`ModifyArray` undefined).
- [ ] **Step 3: Implement** `ModifyArray` + `ValidateArrayOp` + `ApplyArrayOp` in `SerializedMemberHandler.cs` (uses `Skip`/`Abort`/`PlayModeBlocks`/`SerializedTargeting` from 0.7):
```csharp
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

                    var size = sp.arraySize;
                    var hasExpected = op["expectedSize"] != null;
                    if (!hasExpected && !force) { skipped.Add(Skip(rt.Describe, arrayPath, "MISSING_PRECONDITION", "expectedSize required (or force)")); if (allOrNothing) return Abort(skipped); continue; }
                    if (hasExpected && op["expectedSize"].Value<int>() != size) { skipped.Add(Skip(rt.Describe, arrayPath, "STALE_SIZE", "array size changed", size, op["expectedSize"])); if (allOrNothing) return Abort(skipped); continue; }

                    if (!ValidateArrayOp(opName, op, size, out var newSize, out var verr, out var vcode))
                    { skipped.Add(Skip(rt.Describe, arrayPath, vcode, verr)); if (allOrNothing) return Abort(skipped); continue; }

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
                    var el = sp.GetArrayElementAtIndex(ri);
                    bool nonNullRef = el.propertyType == SerializedPropertyType.ObjectReference && el.objectReferenceValue != null;
                    sp.DeleteArrayElementAtIndex(ri);
                    if (nonNullRef) sp.DeleteArrayElementAtIndex(ri); // object-ref arrays: first delete nulls, second removes
                    break;
                case "move": sp.MoveArrayElement(o["index"].Value<int>(), o["toIndex"].Value<int>()); break;
                case "clear": sp.ClearArray(); break;
            }
            if (withoutUndo) so.ApplyModifiedPropertiesWithoutUndo(); else so.ApplyModifiedProperties();
            if (PrefabUtility.IsPartOfPrefabInstance(obj)) PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
            dirty.Add(obj);
        }
```
- [ ] **Step 4:** recompile → EditMode green. **Step 5:** commit `feat(0.8.0): modify_serialized_array — array ops with size-CAS`.

---

## Task 3: catalog + register + floor dogfood

- [ ] **Step 1:** register `dispatcher.Register("modify_serialized_array", SerializedMemberHandler.ModifyArray);` in `BuildDispatcher`.
- [ ] **Step 2:** add the `modify_serialized_array` catalog entry (`sides:["editor"]`, category `serialization`, `destructive:true`; params `ops[]` + force/dryRun/allOrNothing/withoutUndo/undoLabel; result `{applied,changed,skipped}`). Regen + drift.
- [ ] **Step 3:** compat-lint; recompile; EditMode all green; live dogfood on 2020.3 — `inspect` an array, `modify_serialized_array` resize/insert with `expectedSize`, `save_assets`.
- [ ] **Step 4:** commit `feat(0.8.0): catalog + wiring for modify_serialized_array; floor-verified`.

---

## Self-review
- Spec coverage: D8 ops (resize/insert/remove/move/clear) + size-CAS (STALE_SIZE/MISSING_PRECONDITION/force) + bounds (INDEX_OUT_OF_RANGE) + NOT_AN_ARRAY + object-ref double-delete + AnimationCurve write. Correctness reuses 0.7 (Undo group try/finally, prefab, dirty, play-mode). Gradient/SerializeReference deferred (0.9).
- Type consistency: `ModifyArray(JObject)→HandlerOutcome`; `ValidateArrayOp(op,JObject,size,out newSize,out error,out code)`; `ApplyArrayOp(obj,arrayPath,JObject,withoutUndo,undoLabel,dirty)`; reuses `Skip/Abort/PlayModeBlocks/SerializedTargeting`.
- Floor-safe: all array `SerializedProperty` ops + `animationCurveValue` are 2017/2018+. Nothing for COMPATIBILITY.md.
