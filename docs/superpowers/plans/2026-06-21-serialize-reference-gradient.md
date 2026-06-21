# `[SerializeReference]` Writing & `Gradient` (0.9.0) Implementation Plan

> REQUIRED SUB-SKILL: superpowers:executing-plans. Spec: `docs/superpowers/specs/2026-06-21-serialize-reference-gradient-design.md`.
> Both features flow through the existing `set_serialized_properties` — NO new catalog command (drift unchanged).

**Verification loop:** per task — `refresh_assets` → `read-editor-log.mjs` → `run_tests EditMode` → `get_test_results`; `compat-lint`.

---

## Task 1: `Gradient` read + write (reflection)

**Files:** `SerializedValue.cs` (+ `GradientReflection` helper + read/write cases + Color helpers); fixture `GradientField`; `SerializedValueTests.cs`.

- [ ] **Fixture:** add `public Gradient GradientField = new Gradient();` to `SerFixtureAsset` (a default gradient has 2 colorKeys + 2 alphaKeys).
- [ ] **Test:**
```csharp
[Test] public void Gradient_RoundTrips()
{
    var p = _so.FindProperty("GradientField");
    var read = SerializedValue.Read(p);
    Assert.AreNotEqual(JTokenType.Null, read.Type, "gradient read must work on the floor (reflection)");
    Assert.IsTrue(SerializedValue.Write(p, read, out var err), err);
    _so.ApplyModifiedPropertiesWithoutUndo();
    Assert.IsTrue(JToken.DeepEquals(read, SerializedValue.Read(_so.FindProperty("GradientField"))));
}
```
- [ ] **Implement** in `SerializedValue.cs`: a `GradientReflection` helper, a `Gradient` **read** case (replacing the read-only `default`), a `Gradient` **write** case, and shared `ColorObj`/`ColorFrom` helpers (reuse in the existing `Color` cases too).
```csharp
        internal static class GradientReflection
        {
            private static readonly System.Reflection.PropertyInfo Prop =
                typeof(SerializedProperty).GetProperty("gradientValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            public static Gradient Get(SerializedProperty p) => Prop?.GetValue(p) as Gradient;
            public static bool Set(SerializedProperty p, Gradient g) { if (Prop == null) return false; Prop.SetValue(p, g); return true; }
        }
```
Read case (before `default`):
```csharp
                case SerializedPropertyType.Gradient:
                {
                    var g = GradientReflection.Get(p);
                    if (g == null) return JValue.CreateNull();
                    var ck = new JArray(); foreach (var k in g.colorKeys) ck.Add(new JObject { ["color"] = ColorObj(k.color), ["time"] = k.time });
                    var ak = new JArray(); foreach (var k in g.alphaKeys) ak.Add(new JObject { ["alpha"] = k.alpha, ["time"] = k.time });
                    return new JObject { ["colorKeys"] = ck, ["alphaKeys"] = ak, ["mode"] = g.mode.ToString() };
                }
```
Write case (before `default`):
```csharp
                    case SerializedPropertyType.Gradient:
                    {
                        var g = new Gradient();
                        var cks = new System.Collections.Generic.List<GradientColorKey>();
                        foreach (var k in (v["colorKeys"] as JArray ?? new JArray())) cks.Add(new GradientColorKey(ColorFrom(k["color"]), F(k, "time")));
                        var aks = new System.Collections.Generic.List<GradientAlphaKey>();
                        foreach (var k in (v["alphaKeys"] as JArray ?? new JArray())) aks.Add(new GradientAlphaKey(F(k, "alpha"), F(k, "time")));
                        g.SetKeys(cks.ToArray(), aks.ToArray());
                        if (v["mode"] != null && System.Enum.TryParse<GradientMode>(v["mode"].ToString(), out var gm)) g.mode = gm;
                        if (!GradientReflection.Set(p, g)) { error = "TYPE_MISMATCH: gradientValue not accessible on this Unity version"; return false; }
                        return true;
                    }
```
Helpers:
```csharp
        private static JObject ColorObj(Color c) => new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
        private static Color ColorFrom(JToken v) => new Color(F(v, "r"), F(v, "g"), F(v, "b"), v["a"] != null ? F(v, "a") : 1f);
```
(Refactor the existing `Color` read/write to use `ColorObj`/`ColorFrom` — keep behavior identical.)
- [ ] Recompile → EditMode green. Commit `feat(0.9.0): Gradient read/write (reflection on the floor)`.

---

## Task 2: `[SerializeReference]` writing + inspect exposure

**Files:** new `ManagedReferenceResolver.cs`; `SerializedValue.cs` (managed-ref write case); `SerializedMemberHandler.cs` (Tree: emit field typename + recurse a set ref); fixture (interface + 2 impls + `[SerializeReference]` field); `SerializedMemberHandlerTests.cs`.

- [ ] **Fixture** (`SerFixtureAsset.cs`): add at namespace scope —
```csharp
    public interface ISerStrategy { }
    [System.Serializable] public class SerStrategyA : ISerStrategy { public int A = 1; }
    [System.Serializable] public class SerStrategyB : ISerStrategy { public string B = "x"; }
```
and on `SerFixtureAsset`: `[SerializeReference] public ISerStrategy Strategy;`
- [ ] **Tests** (`SerializedMemberHandlerTests`):
```csharp
[Test] public void ManagedRef_SetByType_Instantiates()
{
    var r = SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray {
        new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                      ["set"] = new JObject { ["Strategy"] = new JObject { ["value"] = new JObject { ["$type"] = "UnityEditorMCP.Tests.SerStrategyA" } } } } } });
    Assert.IsFalse(r.IsError, r.Error);
    Assert.IsInstanceOf<SerStrategyA>(_asset.Strategy);
}
[Test] public void ManagedRef_NotAssignable_Rejected()
{
    var r = SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray {
        new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath },
                      ["set"] = new JObject { ["Strategy"] = new JObject { ["value"] = new JObject { ["$type"] = "System.String" } } } } } });
    Assert.AreEqual("TYPE_NOT_ASSIGNABLE", FirstSkipCode(r));
}
[Test] public void ManagedRef_Clear_Null()
{
    SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray { new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["set"] = new JObject { ["Strategy"] = new JObject { ["value"] = new JObject { ["$type"] = "UnityEditorMCP.Tests.SerStrategyA" } } } } } });
    var r = SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray { new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["set"] = new JObject { ["Strategy"] = new JObject { ["value"] = JValue.CreateNull() } } } } });
    Assert.IsFalse(r.IsError, r.Error);
    Assert.IsNull(_asset.Strategy);
}
[Test] public void ManagedRef_NestedFieldWrite()
{
    SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray { new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["set"] = new JObject { ["Strategy"] = new JObject { ["value"] = new JObject { ["$type"] = "UnityEditorMCP.Tests.SerStrategyA" } } } } } });
    var r = SerializedMemberHandler.Set(new JObject { ["force"] = true, ["edits"] = new JArray { new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath }, ["set"] = new JObject { ["Strategy.A"] = new JObject { ["value"] = 42 } } } } });
    Assert.IsFalse(r.IsError, r.Error);
    Assert.AreEqual(42, ((SerStrategyA)_asset.Strategy).A);
}
[Test] public void Inspect_ManagedRef_ExposesFieldTypename()
{
    var data = JObject.FromObject(SerializedMemberHandler.Inspect(new JObject { ["target"] = new JObject { ["assetPath"] = _assetPath } }).Payload);
    var node = FindProp((JArray)data["object"]["properties"], "Strategy");
    Assert.IsNotNull(node);
    Assert.IsNotNull(node["managedReferenceFieldTypename"]);
}
```
- [ ] **Implement `ManagedReferenceResolver.cs`** (`UnityEditorMCP.Handlers`): `TrySet(SerializedProperty, JToken, out error, out code)` + `ResolveType(string)` (handle the `"Assembly FullName"` field-typename format + plain FullName/simple-name; search loaded assemblies, tolerate `ReflectionTypeLoadException`). Validate `constraint.IsAssignableFrom(type)`; instantiate `Activator.CreateInstance` → `FormatterServices.GetUninitializedObject`; assign `p.managedReferenceValue`.
- [ ] **`SerializedValue.Write`** `ManagedReference` case (before `default`): `return ManagedReferenceResolver.TrySet(p, v, out error, out _);` (the resolver sets a richer `code`; surface `error`).
- [ ] **`SerializedMemberHandler.Tree`**: for a `ManagedReference` node also emit `node["managedReferenceFieldTypename"] = it.managedReferenceFieldTypename;`, and let the walk RECURSE into a set managed reference (it has visible children when set) — the existing depth logic already descends `hasVisibleChildren`, so just don't treat ManagedReference as atomic; verify the subtree appears.
- [ ] Recompile → EditMode green. Commit `feat(0.9.0): [SerializeReference] writing + inspect exposure`.

---

## Task 3: COMPATIBILITY.md + floor dogfood

- [ ] Add the `Gradient` reflection workaround + the `managedReference*` API floors to `COMPATIBILITY.md`.
- [ ] `compat-lint` (the reflection access is not an `#if` site; confirm it doesn't trip the linter — if it flags `gradientValue`, the reflection string is fine, but verify). Recompile; EditMode all green; live dogfood on 2020.3 — set a managed reference (`$type`) + a gradient on a real object, read back.
- [ ] Commit `feat(0.9.0): COMPATIBILITY notes + floor verification — Serialization Core complete`.

---

## Self-review
- Spec coverage: D7 (set by `$type` w/ assignability validation, null clear, nested writes, discovery via find_implementations + inspect exposure, instantiation fallback) + Gradient (reflection read/write, structured). Error codes TYPE_NOT_ASSIGNABLE/TYPE_NOT_FOUND. CAS reused. No new tool (drift unchanged).
- Type consistency: `ManagedReferenceResolver.TrySet(SerializedProperty, JToken, out string, out string)`, `ResolveType(string)→Type`; `GradientReflection.Get/Set`; `ColorObj/ColorFrom` shared. Reuses 0.7 `F`.
- Floor-safe: managedReference* (2019.3/2020.1+), Activator/FormatterServices (BCL), Gradient via reflection (COMPATIBILITY.md). No `#if` break.
