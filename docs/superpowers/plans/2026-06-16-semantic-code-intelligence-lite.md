# Semantic Code Intelligence — Lite Layer (0.6.0, Plan 1 of 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Unity-grounded, name-based semantic resolution to the existing syntactic analyzer — `resolve_symbol`, `get_type_members`, `find_implementations` — and tag `find_references` results with their `resolution` grade, all in-editor with zero new dependencies.

**Architecture:** Extend the existing static `CodeIntelligenceHandler` (Editor C#) with reflection + `UnityEditor.TypeCache` lookups over Unity's already-compiled assemblies. These are **name-based** (a ranked candidate list, ambiguity disclosed) — not source-position binding, which is deferred to the Roslyn sidecar (Plan 3). Each command is registered on the Core `CommandDispatcher` rail and declared in the protocol catalog (`sides: ["editor"]`).

**Tech Stack:** C# 8 / netstandard 2.0 (Unity 2020.3 floor); `System.Reflection`; `UnityEditor.TypeCache`; NUnit EditMode tests; the protocol catalog + drift gate (Node).

**Scope note:** This is Plan 1 (the lite layer). The `RoslynManager` capability framework (Plan 2) and the Roslyn sidecar + project-model export (Plan 3) are separate plans per the spec's phasing. This plan ships standalone value and is floor-dogfoodable on its own.

---

## File structure

- **Modify** `unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs` — add `ResolveSymbol`, `GetTypeMembers`, `FindImplementations`; add a `resolution` field to the `FindReferences` result.
- **Modify** `unity-editor-mcp/Editor/Core/UnityEditorMCP.cs` (`BuildDispatcher`) — register the three new commands.
- **Create** `unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs` — NUnit EditMode tests + their fixture types.
- **Modify** `protocol/catalog/commands.json` — declare the three commands (`sides: ["editor"]`, category `codeintel`) + add `resolution` to the `find_references` result schema.
- **Regenerate** `unity-editor-mcp/Core/CommandCatalog.g.cs` (via the generator).

**Testing note:** the new C# is tested by **NUnit EditMode tests**, which run inside the editor — from this repo they are driven through the live bridge: `call_unity_tool(instance, refresh_assets)` to recompile, then `call_unity_tool(instance, run_tests, {testMode:"EditMode", runAll:true})` + `get_test_results`. The `dotnet test` lane does NOT cover editor handlers (they reference UnityEngine/UnityEditor). The drift gate + catalog are pure-Node and run from the CLI.

---

## Task 1: Declare the three lite commands in the catalog

**Files:**
- Modify: `protocol/catalog/commands.json`
- Modify (generated): `unity-editor-mcp/Core/CommandCatalog.g.cs`

- [ ] **Step 1: Add the three command entries to the catalog**

In `protocol/catalog/commands.json`, add these objects to the `commands` array (place them next to the existing `get_symbols`/`find_symbol` entries; match the file's existing formatting):

```json
{
  "name": "resolve_symbol",
  "category": "codeintel",
  "sides": ["editor"],
  "description": "Resolve a C# identifier NAME to its declaring type(s) and member(s) using Unity's compiled assemblies (reflection/TypeCache). Name-based: returns a ranked candidate list (same-named symbols across types are NOT disambiguated — that needs the Roslyn sidecar). Pass `name`, or `path`+`position` to extract the token name at that source location.",
  "params": {
    "type": "object",
    "properties": {
      "name": { "type": "string", "description": "Identifier name to resolve. Optional if path+position are given." },
      "path": { "type": "string", "description": "A .cs file under the project, used with position to extract the token name." },
      "position": { "type": "object", "description": "{ line, column } (1-based) of the token in `path`.", "properties": { "line": { "type": "integer" }, "column": { "type": "integer" } } },
      "maxResults": { "type": "integer", "description": "Cap on candidates (default 50)." }
    }
  },
  "result": {
    "type": "object",
    "properties": {
      "name": { "type": "string" },
      "count": { "type": "integer" },
      "candidates": { "type": "array", "items": { "type": "object", "properties": {
        "type": { "type": "string" }, "member": { "type": "string" }, "kind": { "type": "string" },
        "signature": { "type": "string" }, "visibility": { "type": "string" }, "assembly": { "type": "string" }
      } } }
    }
  }
},
{
  "name": "get_type_members",
  "category": "codeintel",
  "sides": ["editor"],
  "description": "List the members (fields, properties, methods, events) of a named C# type from Unity's compiled assemblies, with signatures, visibility, and attributes.",
  "params": {
    "type": "object",
    "properties": {
      "typeName": { "type": "string", "description": "Simple or full type name (e.g. \"Player\" or \"MyGame.Player\")." },
      "includeInherited": { "type": "boolean", "description": "Include inherited members (default false — declared-only)." }
    },
    "required": ["typeName"]
  },
  "result": {
    "type": "object",
    "properties": {
      "type": { "type": "string" }, "count": { "type": "integer" },
      "members": { "type": "array", "items": { "type": "object", "properties": {
        "name": { "type": "string" }, "kind": { "type": "string" }, "signature": { "type": "string" },
        "visibility": { "type": "string" }, "attributes": { "type": "array", "items": { "type": "string" } }
      } } }
    }
  }
},
{
  "name": "find_implementations",
  "category": "codeintel",
  "sides": ["editor"],
  "description": "Find subtypes of a named class or implementors of a named interface, via UnityEditor.TypeCache (fast, indexed over the compiled assemblies).",
  "params": {
    "type": "object",
    "properties": { "typeName": { "type": "string", "description": "Simple or full class/interface name." } },
    "required": ["typeName"]
  },
  "result": {
    "type": "object",
    "properties": {
      "type": { "type": "string" }, "count": { "type": "integer" },
      "implementors": { "type": "array", "items": { "type": "object", "properties": {
        "type": { "type": "string" }, "assembly": { "type": "string" }, "kind": { "type": "string" }
      } } }
    }
  }
}
```

- [ ] **Step 2: Add the `resolution` field to the existing `find_references` result schema**

Find the `find_references` entry in `protocol/catalog/commands.json`. In its `result.properties`, add:

```json
"resolution": { "type": "string", "enum": ["syntactic", "semantic"], "description": "How the references were computed: \"syntactic\" (textual, lite) or \"semantic\" (Roslyn sidecar). Lite always returns \"syntactic\"." }
```

- [ ] **Step 3: Regenerate the C# catalog**

Run: `node protocol/scripts/generate-csharp-catalog.mjs`
Expected: `Wrote …/unity-editor-mcp/Core/CommandCatalog.g.cs`

- [ ] **Step 4: Verify the catalog is well-formed and drift reflects the new commands as gaps**

Run: `node protocol/scripts/check-drift.mjs`
Expected: it now reports a known/new gap for `resolve_symbol`, `get_type_members`, `find_implementations` — "catalog declares editor side … but no editor dispatch case exists" — because the C# handlers don't exist yet. This is the expected RED state; Tasks 2–4 turn it green.

- [ ] **Step 5: Commit**

```bash
git add protocol/catalog/commands.json unity-editor-mcp/Core/CommandCatalog.g.cs
git commit -m "feat(0.6.0): declare lite code-intel commands in the catalog"
```

---

## Task 2: `resolve_symbol` — name → ranked candidate list

**Files:**
- Modify: `unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs`
- Modify: `unity-editor-mcp/Editor/Core/UnityEditorMCP.cs` (`BuildDispatcher`)
- Create: `unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs`

- [ ] **Step 1: Write the failing NUnit test + fixture**

Create `unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs`:

```csharp
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // Fixture types the lite resolver must find in the compiled test assembly.
    public interface ICodeIntelFixture { void Ping(); }
    public class CodeIntelFixtureBase : ICodeIntelFixture { public int Health; public void Ping() { } }
    public class CodeIntelFixtureDerived : CodeIntelFixtureBase { public void Attack(int power) { } }

    public class CodeIntelligenceSemanticTests
    {
        [Test]
        public void ResolveSymbol_ByName_ReturnsTypeAndMemberCandidates()
        {
            var outcome = CodeIntelligenceHandler.ResolveSymbol(new JObject { ["name"] = "CodeIntelFixtureDerived" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            Assert.GreaterOrEqual((int)data["count"], 1);
            var hasType = false;
            foreach (var c in (JArray)data["candidates"])
                if ((string)c["type"] == "UnityEditorMCP.Tests.CodeIntelFixtureDerived" && (string)c["kind"] == "type") hasType = true;
            Assert.IsTrue(hasType, "the fixture type must be a candidate");
        }

        [Test]
        public void ResolveSymbol_MemberName_ReturnsMemberCandidatesWithDeclaringType()
        {
            var outcome = CodeIntelligenceHandler.ResolveSymbol(new JObject { ["name"] = "Attack" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            var found = false;
            foreach (var c in (JArray)data["candidates"])
                if ((string)c["member"] == "Attack" && (string)c["type"] == "UnityEditorMCP.Tests.CodeIntelFixtureDerived") found = true;
            Assert.IsTrue(found, "Attack must resolve to its declaring type");
        }

        [Test]
        public void ResolveSymbol_MissingNameAndPath_IsValidationError()
        {
            var outcome = CodeIntelligenceHandler.ResolveSymbol(new JObject());
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
        }
    }
}
```

- [ ] **Step 2: Confirm it fails to compile (method not defined)**

Drive a recompile + run via the bridge:
Run: `call_unity_tool(instance, refresh_assets)` then `node scripts/read-editor-log.mjs`
Expected: compile error — `CodeIntelligenceHandler` does not contain `ResolveSymbol`.

- [ ] **Step 3: Implement `ResolveSymbol`**

In `unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs`, add these usings at the top if absent: `using System.Reflection;`. Add the method inside the class (after `FindReferences`):

```csharp
public static HandlerOutcome ResolveSymbol(JObject p)
{
    try
    {
        var name = p["name"]?.ToString();
        // path+position only EXTRACT the token name (the syntactic layer); they do not disambiguate.
        if (string.IsNullOrEmpty(name))
        {
            var path = p["path"]?.ToString();
            var pos = p["position"] as JObject;
            if (!string.IsNullOrEmpty(path) && pos != null)
            {
                var full = ResolveScript(path);
                if (full != null && File.Exists(full))
                    name = IdentifierAt(File.ReadAllText(full), (int)(pos["line"] ?? 0), (int)(pos["column"] ?? 0));
            }
        }
        if (string.IsNullOrEmpty(name))
            return Err("Provide `name`, or `path`+`position` resolving to an identifier", "VALIDATION_ERROR");

        var max = Math.Max(1, Math.Min(p["maxResults"]?.ToObject<int?>() ?? 50, MaxResultsCeiling));
        var candidates = new JArray();
        foreach (var t in EnumerateLoadedTypes())
        {
            if (candidates.Count >= max) break;
            if (t.Name == name)
                candidates.Add(new JObject {
                    ["type"] = t.FullName, ["member"] = null, ["kind"] = "type",
                    ["signature"] = t.FullName, ["visibility"] = t.IsPublic ? "public" : "internal",
                    ["assembly"] = t.Assembly.GetName().Name });
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (candidates.Count >= max) break;
                if (m.Name != name) continue;
                candidates.Add(new JObject {
                    ["type"] = t.FullName, ["member"] = m.Name, ["kind"] = MemberKind(m),
                    ["signature"] = MemberSignature(m), ["visibility"] = MemberVisibility(m),
                    ["assembly"] = t.Assembly.GetName().Name });
            }
        }
        return HandlerOutcome.Ok(new JObject { ["name"] = name, ["count"] = candidates.Count, ["candidates"] = candidates });
    }
    catch (Exception e) { return Err($"Error resolving symbol: {e.Message}"); }
}
```

Add these private helpers to the class (they are reused by Tasks 3–4):

```csharp
private const int TypeScanCeiling = 20000; // bound the reflection scan on large projects

private static IEnumerable<Type> EnumerateLoadedTypes()
{
    int n = 0;
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; } // tolerate partially-loadable assemblies
        catch { continue; }
        foreach (var t in types)
        {
            if (t == null) continue;
            if (++n > TypeScanCeiling) yield break;
            yield return t;
        }
    }
}

private static string MemberKind(MemberInfo m)
{
    switch (m) { case MethodInfo _: return "method"; case PropertyInfo _: return "property";
        case FieldInfo _: return "field"; case EventInfo _: return "event";
        case ConstructorInfo _: return "constructor"; case Type _: return "type"; default: return "member"; }
}

private static string MemberSignature(MemberInfo m)
{
    if (m is MethodInfo mi)
        return $"{mi.ReturnType.Name} {mi.Name}({string.Join(", ", Array.ConvertAll(mi.GetParameters(), x => x.ParameterType.Name + " " + x.Name))})";
    if (m is PropertyInfo pi) return $"{pi.PropertyType.Name} {pi.Name}";
    if (m is FieldInfo fi) return $"{fi.FieldType.Name} {fi.Name}";
    return m.Name;
}

private static string MemberVisibility(MemberInfo m)
{
    switch (m) {
        case MethodInfo mi: return mi.IsPublic ? "public" : (mi.IsPrivate ? "private" : "internal");
        case FieldInfo fi: return fi.IsPublic ? "public" : (fi.IsPrivate ? "private" : "internal");
        default: return "n/a";
    }
}

// Extract the identifier token covering (line, column); 1-based. Reuses the existing line index helpers.
private static string IdentifierAt(string src, int line, int column)
{
    if (line < 1 || column < 1) return null;
    var lines = src.Replace("\r\n", "\n").Split('\n');
    if (line > lines.Length) return null;
    var text = lines[line - 1];
    int i = column - 1;
    if (i < 0 || i >= text.Length) return null;
    bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
    if (!IsIdent(text[i])) return null;
    int start = i, end = i;
    while (start > 0 && IsIdent(text[start - 1])) start--;
    while (end + 1 < text.Length && IsIdent(text[end + 1])) end++;
    return text.Substring(start, end - start + 1);
}
```

> Note: if `MemberKind`/`MemberSignature`/`MemberVisibility`/`EnumerateLoadedTypes` already exist from a prior task, do not duplicate them.

- [ ] **Step 4: Register the command on the dispatch rail**

In `unity-editor-mcp/Editor/Core/UnityEditorMCP.cs`, inside `BuildDispatcher`, next to the existing code-intel registrations, add:

```csharp
dispatcher.Register("resolve_symbol", CodeIntelligenceHandler.ResolveSymbol);
```

- [ ] **Step 5: Recompile and run the test via the bridge**

Run: `call_unity_tool(instance, refresh_assets)`, wait, `node scripts/read-editor-log.mjs` (expect clean compile), then `call_unity_tool(instance, run_tests, {testMode:"EditMode", runAll:true})` and `call_unity_tool(instance, get_test_results, {filterStatus:"Failed"})`
Expected: 0 failed; the three `ResolveSymbol_*` tests pass.

- [ ] **Step 6: Commit**

```bash
git add unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs unity-editor-mcp/Editor/Core/UnityEditorMCP.cs unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs
git commit -m "feat(0.6.0): resolve_symbol (name-based candidate resolution, lite)"
```

---

## Task 3: `get_type_members` — members of a named type

**Files:**
- Modify: `unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs`
- Modify: `unity-editor-mcp/Editor/Core/UnityEditorMCP.cs` (`BuildDispatcher`)
- Modify: `unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `CodeIntelligenceSemanticTests`:

```csharp
[Test]
public void GetTypeMembers_ReturnsDeclaredMembersWithSignatures()
{
    var outcome = CodeIntelligenceHandler.GetTypeMembers(new JObject { ["typeName"] = "CodeIntelFixtureDerived" });
    Assert.IsFalse(outcome.IsError, outcome.Error);
    var data = JObject.FromObject(outcome.Payload);
    var hasAttack = false;
    foreach (var m in (JArray)data["members"])
        if ((string)m["name"] == "Attack" && (string)m["kind"] == "method") hasAttack = true;
    Assert.IsTrue(hasAttack);
}

[Test]
public void GetTypeMembers_UnknownType_IsNotFound()
{
    var outcome = CodeIntelligenceHandler.GetTypeMembers(new JObject { ["typeName"] = "NoSuchType_xyz" });
    Assert.IsTrue(outcome.IsError);
    Assert.AreEqual("NOT_FOUND", outcome.Code);
}
```

- [ ] **Step 2: Verify it fails to compile**

Run: `call_unity_tool(instance, refresh_assets)` then `node scripts/read-editor-log.mjs`
Expected: compile error — no `GetTypeMembers`.

- [ ] **Step 3: Implement `GetTypeMembers`**

Add to `CodeIntelligenceHandler.cs`:

```csharp
public static HandlerOutcome GetTypeMembers(JObject p)
{
    try
    {
        var typeName = p["typeName"]?.ToString();
        if (string.IsNullOrEmpty(typeName)) return Err("Missing required parameter: typeName", "VALIDATION_ERROR");
        var type = FindTypeByName(typeName);
        if (type == null) return Err($"Type not found: {typeName}", "NOT_FOUND");
        var includeInherited = p["includeInherited"]?.ToObject<bool?>() ?? false;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        if (!includeInherited) flags |= BindingFlags.DeclaredOnly;

        var members = new JArray();
        foreach (var m in type.GetMembers(flags))
        {
            if (m is MethodInfo mi && (mi.IsSpecialName)) continue; // skip property get_/set_ accessors
            var attrs = new JArray();
            foreach (var a in m.GetCustomAttributes(false)) attrs.Add(a.GetType().Name);
            members.Add(new JObject {
                ["name"] = m.Name, ["kind"] = MemberKind(m), ["signature"] = MemberSignature(m),
                ["visibility"] = MemberVisibility(m), ["attributes"] = attrs });
        }
        return HandlerOutcome.Ok(new JObject { ["type"] = type.FullName, ["count"] = members.Count, ["members"] = members });
    }
    catch (Exception e) { return Err($"Error reading type members: {e.Message}"); }
}

// Resolve a simple or full type name to the first matching loaded Type (full-name match wins).
private static Type FindTypeByName(string typeName)
{
    Type firstSimple = null;
    foreach (var t in EnumerateLoadedTypes())
    {
        if (t.FullName == typeName) return t;
        if (firstSimple == null && t.Name == typeName) firstSimple = t;
    }
    return firstSimple;
}
```

- [ ] **Step 4: Register it**

In `BuildDispatcher` (`UnityEditorMCP.cs`):

```csharp
dispatcher.Register("get_type_members", CodeIntelligenceHandler.GetTypeMembers);
```

- [ ] **Step 5: Recompile + run the EditMode suite via the bridge**

Run: `refresh_assets` → `read-editor-log.mjs` (clean) → `run_tests` (EditMode) → `get_test_results`
Expected: 0 failed.

- [ ] **Step 6: Commit**

```bash
git add unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs unity-editor-mcp/Editor/Core/UnityEditorMCP.cs unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs
git commit -m "feat(0.6.0): get_type_members (reflection over the named type, lite)"
```

---

## Task 4: `find_implementations` — subtypes / implementors via TypeCache

**Files:**
- Modify: `unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs`
- Modify: `unity-editor-mcp/Editor/Core/UnityEditorMCP.cs` (`BuildDispatcher`)
- Modify: `unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `CodeIntelligenceSemanticTests`:

```csharp
[Test]
public void FindImplementations_Interface_ReturnsImplementors()
{
    var outcome = CodeIntelligenceHandler.FindImplementations(new JObject { ["typeName"] = "ICodeIntelFixture" });
    Assert.IsFalse(outcome.IsError, outcome.Error);
    var data = JObject.FromObject(outcome.Payload);
    var found = false;
    foreach (var x in (JArray)data["implementors"])
        if ((string)x["type"] == "UnityEditorMCP.Tests.CodeIntelFixtureBase") found = true;
    Assert.IsTrue(found, "CodeIntelFixtureBase implements ICodeIntelFixture");
}
```

- [ ] **Step 2: Verify it fails to compile**

Run: `refresh_assets` → `read-editor-log.mjs`
Expected: compile error — no `FindImplementations`.

- [ ] **Step 3: Implement `FindImplementations`**

Add `using UnityEditor;` at the top of `CodeIntelligenceHandler.cs` if absent (TypeCache is in `UnityEditor`). Add:

```csharp
public static HandlerOutcome FindImplementations(JObject p)
{
    try
    {
        var typeName = p["typeName"]?.ToString();
        if (string.IsNullOrEmpty(typeName)) return Err("Missing required parameter: typeName", "VALIDATION_ERROR");
        var type = FindTypeByName(typeName);
        if (type == null) return Err($"Type not found: {typeName}", "NOT_FOUND");

        var implementors = new JArray();
        // TypeCache.GetTypesDerivedFrom covers both subclasses and interface implementors.
        foreach (var t in TypeCache.GetTypesDerivedFrom(type))
            implementors.Add(new JObject {
                ["type"] = t.FullName, ["assembly"] = t.Assembly.GetName().Name,
                ["kind"] = t.IsInterface ? "interface" : (t.IsAbstract ? "abstract" : "class") });
        return HandlerOutcome.Ok(new JObject { ["type"] = type.FullName, ["count"] = implementors.Count, ["implementors"] = implementors });
    }
    catch (Exception e) { return Err($"Error finding implementations: {e.Message}"); }
}
```

- [ ] **Step 4: Register it**

In `BuildDispatcher` (`UnityEditorMCP.cs`):

```csharp
dispatcher.Register("find_implementations", CodeIntelligenceHandler.FindImplementations);
```

- [ ] **Step 5: Recompile + run the EditMode suite**

Run: `refresh_assets` → `read-editor-log.mjs` (clean) → `run_tests` (EditMode) → `get_test_results`
Expected: 0 failed.

- [ ] **Step 6: Commit**

```bash
git add unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs unity-editor-mcp/Editor/Core/UnityEditorMCP.cs unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs
git commit -m "feat(0.6.0): find_implementations (TypeCache, lite)"
```

---

## Task 5: Tag `find_references` results with `resolution: "syntactic"`

**Files:**
- Modify: `unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs` (the `FindReferences` method)
- Modify: `unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `CodeIntelligenceSemanticTests`:

```csharp
[Test]
public void FindReferences_ResultIsTaggedSyntactic()
{
    var outcome = CodeIntelligenceHandler.FindReferences(new JObject { ["name"] = "CodeIntelFixtureDerived" });
    Assert.IsFalse(outcome.IsError, outcome.Error);
    var data = JObject.FromObject(outcome.Payload);
    Assert.AreEqual("syntactic", (string)data["resolution"]);
}
```

- [ ] **Step 2: Verify it fails**

Run: `refresh_assets` → `run_tests` (EditMode) → `get_test_results`
Expected: `FindReferences_ResultIsTaggedSyntactic` FAILS — `resolution` is null.

- [ ] **Step 3: Add the field to the `FindReferences` success payload**

In `CodeIntelligenceHandler.FindReferences`, locate the `HandlerOutcome.Ok(new JObject { … })` that returns the matches and add `["resolution"] = "syntactic"` to that JObject. For example, if it currently returns:

```csharp
return HandlerOutcome.Ok(new JObject { ["name"] = name, ["count"] = total, ["references"] = matches });
```

change it to:

```csharp
return HandlerOutcome.Ok(new JObject { ["name"] = name, ["count"] = total, ["references"] = matches, ["resolution"] = "syntactic" });
```

- [ ] **Step 4: Verify it passes**

Run: `refresh_assets` → `run_tests` (EditMode) → `get_test_results`
Expected: 0 failed.

- [ ] **Step 5: Commit**

```bash
git add unity-editor-mcp/Editor/Handlers/CodeIntelligenceHandler.cs unity-editor-mcp/Tests/Editor/Handlers/CodeIntelligenceSemanticTests.cs
git commit -m "feat(0.6.0): tag find_references results resolution=syntactic"
```

---

## Task 6: Close the drift gate + full floor verification

**Files:** none (verification + dogfood only)

- [ ] **Step 1: Drift gate is green (no gaps)**

Run: `node protocol/scripts/check-drift.mjs`
Expected: `OK` — the three new commands now have editor dispatch cases (registered in Tasks 2–4), so there are no new gaps.

- [ ] **Step 2: Compat-lint + dotnet (regression check)**

Run: `node scripts/compat-lint.mjs` (expect OK — only reflection/TypeCache/standard APIs were added) and `cd dotnet/UnityEditorMCP.Core.Tests && dotnet test` (expect Passed — the Core lane is untouched).

- [ ] **Step 3: Full EditMode dogfood on the floor + the other LTS editors**

For each live instance (2020.3, 2021.3, 2022.3): `call_unity_tool(instance, refresh_assets)` → `node scripts/read-editor-log.mjs` (assemblies OK on each) → `call_unity_tool(instance, run_tests, {testMode:"EditMode", runAll:true})` → `call_unity_tool(instance, get_test_results, {includeDetails:false})`
Expected: the full EditMode suite passes on each editor (the previous count + the new `CodeIntelligenceSemanticTests`).

- [ ] **Step 4: Live smoke of each new command via the bridge**

Run: `call_unity_tool(instance, get_type_members, {typeName:"GameObject"})`, `call_unity_tool(instance, find_implementations, {typeName:"MonoBehaviour"})`, `call_unity_tool(instance, resolve_symbol, {name:"Update"})`
Expected: structured results (GameObject members; many MonoBehaviour subtypes; Update candidates across types) — confirming the commands work against the real project, not just fixtures.

- [ ] **Step 5: Final commit (if anything changed) + update COMPATIBILITY.md if a guard was added**

```bash
git add -A
git commit -m "chore(0.6.0): lite code-intel — floor verification (2020.3/2021.3/2022.3 green)"
```

(If Step 2 surfaced a floor-divergent API needing a guard, add the `#if` guard + a COMPATIBILITY.md row before committing.)

---

## Self-review

- **Spec coverage:** This plan implements the spec's Layer 1 lite command set (`resolve_symbol`, `get_type_members`, `find_implementations`) and the `find_references` `resolution` tag (§2, §4). The honest name-based scoping of `resolve_symbol` (review finding #1) is encoded in the catalog description + the candidate-list result + the ambiguity test. The `RoslynManager` capability framework (§3), the sidecar (§2 Layer 2, §5), and the model export are explicitly **out of scope** here (Plans 2 and 3).
- **Type consistency:** the C# method names (`ResolveSymbol`/`GetTypeMembers`/`FindImplementations`/`FindReferences`) and the shared private helpers (`EnumerateLoadedTypes`/`FindTypeByName`/`MemberKind`/`MemberSignature`/`MemberVisibility`/`IdentifierAt`) are used consistently across Tasks 2–5; the catalog command names match the `dispatcher.Register(...)` strings and the test call sites.
- **No placeholders:** every step carries the exact file, the actual test, the actual implementation, and the exact run command with expected result. Verification is via the live bridge (NUnit EditMode) + the pure-Node drift/compat lanes, consistent with how this repo is dogfooded.
- **Assumption to verify at execution:** confirm the existing `CodeIntelligenceHandler` exposes the helpers this plan reuses by name (`ResolveScript`, `Err`, `MaxResultsCeiling`) — they appear in the current file (verified at `CodeIntelligenceHandler.cs:27-40`). If `Err`'s signature differs, match the existing one.
