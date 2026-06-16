# Semantic Code Intelligence — Roslyn Sidecar (0.6.0, Plan 3 of 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the opt-in .NET Roslyn sidecar that makes Plan 2's gated commands real — `find_references` (semantic), `goto_definition`, `rename_symbol`, `get_diagnostics`, `get_type_hierarchy` — fed by the editor's exported `CompilationPipeline` model, spawned by a lazy-downloading Node client.

**Architecture:** A standalone `dotnet/UnityEditorMCP.Roslyn` console app (`net8.0`, `Microsoft.CodeAnalysis.CSharp` + `…Workspaces`) speaks newline-delimited JSON-RPC over stdio. The Unity editor exports a project model (source paths + reference dll paths + defines + langVersion) to `Library/UnityEditorMCP/roslyn-model.json`; the sidecar builds an `AdhocWorkspace` from it. The Node client (`roslynSidecar.js`) lazy-downloads the self-contained binary from the public GitHub release on first `start_roslyn`, caches it, spawns it, and becomes the `RoslynManager` client factory (replacing Plan 2's `null` default). Base npm install stays .NET-free.

**Tech Stack:** .NET 8 + Roslyn (`Microsoft.CodeAnalysis.CSharp.Workspaces`); xUnit; Node `child_process` + `https`; Unity `UnityEditor.Compilation.CompilationPipeline` (C# 8 / 2020.3 floor-safe); GitHub Actions (build + release the per-platform binaries).

**Depends on:** Plan 2 (the `RoslynManager` + `roslynTools` + the routing) — this plan supplies the real client factory + the editor export + the sidecar.

---

## Decisions (from the design pass)

- **Distribution:** lazy-download the platform's self-contained single-file binary from the public GitHub release on first `start_roslyn`; cache under the OS cache dir; verify a SHA-256 from a published `manifest.json`; spawn. Absent network / unsupported platform → `start_roslyn` returns `unavailable` (lite still works). Base install ships no .NET.
- **RPC:** newline-delimited JSON over stdio — request `{id, method, params}`, response `{id, result}` or `{id, error:{code,message}}`. stderr = the sidecar's log.
- **Model:** the editor writes `roslyn-model.json` (`{generation, assemblies:[{name, sourceFiles[], references[], defines[], langVersion}]}`); the sidecar parses sources + `MetadataReference.CreateFromFile` for references → one `Compilation` per assembly in an `AdhocWorkspace`.
- **Floor:** the editor export uses only `CompilationPipeline` (2017.3+) + `System.IO` → C# 8 / 2020.3-safe. The sidecar is out-of-process modern .NET → not floor-coupled.

---

## File structure

- **Create** `dotnet/UnityEditorMCP.Roslyn/UnityEditorMCP.Roslyn.csproj` — the sidecar app (net8.0, PublishSingleFile + self-contained).
- **Create** `dotnet/UnityEditorMCP.Roslyn/Program.cs` — the stdio JSON-RPC loop + method dispatch.
- **Create** `dotnet/UnityEditorMCP.Roslyn/WorkspaceModel.cs` — model JSON DTOs + `WorkspaceBuilder` (model → `AdhocWorkspace`/`Solution`).
- **Create** `dotnet/UnityEditorMCP.Roslyn/Commands.cs` — the 5 semantic command implementations.
- **Create** `dotnet/UnityEditorMCP.Roslyn.Tests/*.csproj` + `CommandsTests.cs` — xUnit over fixtures.
- **Create** `unity-editor-mcp/Editor/Handlers/RoslynModelExporter.cs` — the editor-side model export (a command).
- **Modify** `unity-editor-mcp/Editor/Core/UnityEditorMCP.cs` — register `export_roslyn_model`.
- **Modify** `protocol/catalog/commands.json` + regen — declare `export_roslyn_model` (`sides:["editor"]`).
- **Create** `mcp-server/src/core/roslynSidecar.js` — the lazy-download + spawn + JSON-RPC client + the factory.
- **Modify** `mcp-server/src/core/roslynManager.js` — default the factory to the real sidecar factory.
- **Create** `.github/workflows/roslyn-sidecar-release.yml` — build + attach per-platform binaries + `manifest.json` on a `roslyn-vX` tag.
- **Create** `mcp-server/tests/unit/core/roslynSidecar.test.js` — client unit tests (protocol framing, download guard) against a stub child.

---

## Task 1: Sidecar project skeleton + the stdio JSON-RPC loop

**Files:**
- Create: `dotnet/UnityEditorMCP.Roslyn/UnityEditorMCP.Roslyn.csproj`, `Program.cs`
- Create: `dotnet/UnityEditorMCP.Roslyn.Tests/UnityEditorMCP.Roslyn.Tests.csproj`, `ProtocolTests.cs`

- [ ] **Step 1: Create the csproj**

`dotnet/UnityEditorMCP.Roslyn/UnityEditorMCP.Roslyn.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>unity-editor-mcp-roslyn</AssemblyName>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.11.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing protocol test**

`dotnet/UnityEditorMCP.Roslyn.Tests/UnityEditorMCP.Roslyn.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.6.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.6" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../UnityEditorMCP.Roslyn/UnityEditorMCP.Roslyn.csproj" />
  </ItemGroup>
</Project>
```

`dotnet/UnityEditorMCP.Roslyn.Tests/ProtocolTests.cs`:

```csharp
using UnityEditorMCP.Roslyn;
using Xunit;

public class ProtocolTests
{
    [Fact]
    public void Dispatch_UnknownMethod_ReturnsError()
    {
        var rpc = new RpcServer(new Workspace());
        var resp = rpc.Handle("{\"id\":\"1\",\"method\":\"no_such\",\"params\":{}}");
        Assert.Contains("\"error\"", resp);
        Assert.Contains("\"1\"", resp);
    }

    [Fact]
    public void Dispatch_Ping_ReturnsResult()
    {
        var rpc = new RpcServer(new Workspace());
        var resp = rpc.Handle("{\"id\":\"2\",\"method\":\"ping\",\"params\":{}}");
        Assert.Contains("\"result\"", resp);
        Assert.DoesNotContain("\"error\"", resp);
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `cd dotnet/UnityEditorMCP.Roslyn.Tests && dotnet test`
Expected: build failure — `RpcServer`/`Workspace` undefined.

- [ ] **Step 4: Implement `Program.cs` + `RpcServer`**

`dotnet/UnityEditorMCP.Roslyn/Program.cs`:

```csharp
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace UnityEditorMCP.Roslyn;

public static class Program
{
    public static async Task Main()
    {
        var workspace = new AdhocWorkspace();
        var rpc = new RpcServer(workspace);
        Console.Error.WriteLine("[roslyn-sidecar] ready");
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            if (line.Length == 0) continue;
            string response;
            try { response = await rpc.HandleAsync(line); }
            catch (Exception e) { response = RpcServer.ErrorEnvelope(null, "INTERNAL", e.Message); }
            await Console.Out.WriteLineAsync(response); // newline-delimited
            await Console.Out.FlushAsync();
        }
    }
}

public sealed class RpcServer
{
    private readonly Workspace _workspace;
    private Solution? _solution; // set by load_model

    public RpcServer(Workspace workspace) { _workspace = workspace; }

    // Sync entry for tests; production uses HandleAsync.
    public string Handle(string json) => HandleAsync(json).GetAwaiter().GetResult();

    public async Task<string> HandleAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;
        var pars = root.TryGetProperty("params", out var pEl) ? pEl : default;
        try
        {
            object? result = method switch
            {
                "ping" => new { pong = true },
                "load_model" => await Commands.LoadModelAsync(this, pars),
                "find_references" => await Commands.FindReferencesAsync(_solution, pars),
                "goto_definition" => await Commands.GotoDefinitionAsync(_solution, pars),
                "get_diagnostics" => Commands.GetDiagnostics(_solution, pars),
                "rename_symbol" => await Commands.RenameSymbolAsync(_solution, pars),
                "get_type_hierarchy" => await Commands.GetTypeHierarchyAsync(_solution, pars),
                _ => throw new RpcException("UNKNOWN_METHOD", $"Unknown method: {method}"),
            };
            return JsonSerializer.Serialize(new RpcOk(id, result));
        }
        catch (RpcException re) { return ErrorEnvelope(id, re.Code, re.Message); }
    }

    internal void SetSolution(Solution s) => _solution = s;
    internal Workspace HostWorkspace => _workspace;

    public static string ErrorEnvelope(string? id, string code, string message) =>
        JsonSerializer.Serialize(new RpcErr(id, new RpcErrBody(code, message)));
}

public sealed record RpcOk(string? id, object? result);
public sealed record RpcErr(string? id, RpcErrBody error);
public sealed record RpcErrBody(string code, string message);
public sealed class RpcException : Exception
{
    public string Code { get; }
    public RpcException(string code, string message) : base(message) { Code = code; }
}
```

(Add a minimal `Commands` stub with each method `=> throw new RpcException("NOT_IMPLEMENTED", method)` so this compiles; Tasks 2–7 fill them in. The `ping`/unknown paths are enough for Task 1's tests.)

- [ ] **Step 5: Run it to verify it passes**

Run: `cd dotnet/UnityEditorMCP.Roslyn.Tests && dotnet test`
Expected: PASS (2 tests). Also `cd dotnet/UnityEditorMCP.Roslyn && dotnet build` succeeds.

- [ ] **Step 6: Commit**

```bash
git add dotnet/UnityEditorMCP.Roslyn dotnet/UnityEditorMCP.Roslyn.Tests
git commit -m "feat(0.6.0): Roslyn sidecar skeleton — stdio JSON-RPC loop (Plan 3)"
```

---

## Task 2: Model load → `AdhocWorkspace`/`Solution`

**Files:**
- Create: `dotnet/UnityEditorMCP.Roslyn/WorkspaceModel.cs`
- Create/modify: `dotnet/UnityEditorMCP.Roslyn/Commands.cs`
- Modify: `dotnet/UnityEditorMCP.Roslyn.Tests/` (add `WorkspaceTests.cs` + a fixture model)

- [ ] **Step 1: Write the failing test**

`dotnet/UnityEditorMCP.Roslyn.Tests/WorkspaceTests.cs`:

```csharp
using System.Text.Json;
using UnityEditorMCP.Roslyn;
using Xunit;

public class WorkspaceTests
{
    private static string WriteFixture(out string srcPath)
    {
        var dir = Directory.CreateTempSubdirectory("roslyn-fix").FullName;
        srcPath = Path.Combine(dir, "Player.cs");
        File.WriteAllText(srcPath, "namespace G { public class Player { public int Health; public void Hit(){ Health--; } } }");
        var model = new { generation = 1, assemblies = new[] { new { name = "Assembly-CSharp", sourceFiles = new[] { srcPath }, references = Array.Empty<string>(), defines = Array.Empty<string>(), langVersion = "8.0" } } };
        return JsonSerializer.Serialize(model);
    }

    [Fact]
    public void LoadModel_BuildsCompilationWithTheSource()
    {
        var json = WriteFixture(out _);
        using var jdoc = JsonDocument.Parse("{\"modelJson\":" + JsonSerializer.Serialize(json) + "}");
        var solution = WorkspaceBuilder.Build(json);
        Assert.Single(solution.Projects);
        var comp = solution.Projects.First().GetCompilationAsync().GetAwaiter().GetResult()!;
        var type = comp.GetTypeByMetadataName("G.Player");
        Assert.NotNull(type);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd dotnet/UnityEditorMCP.Roslyn.Tests && dotnet test --filter WorkspaceTests`
Expected: FAIL — `WorkspaceBuilder` undefined.

- [ ] **Step 3: Implement `WorkspaceModel.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UnityEditorMCP.Roslyn;

public sealed record ProjectModel(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sourceFiles")] string[] SourceFiles,
    [property: JsonPropertyName("references")] string[] References,
    [property: JsonPropertyName("defines")] string[] Defines,
    [property: JsonPropertyName("langVersion")] string? LangVersion);

public sealed record RoslynModel(
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("assemblies")] ProjectModel[] Assemblies);

public static class WorkspaceBuilder
{
    public static Solution Build(string modelJson)
    {
        var model = JsonSerializer.Deserialize<RoslynModel>(modelJson)
                    ?? throw new RpcException("BAD_MODEL", "model json did not deserialize");
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var asm in model.Assemblies)
        {
            var projectId = ProjectId.CreateNewId(asm.Name);
            var langVersion = LanguageVersion.CSharp8;
            if (asm.LangVersion != null && LanguageVersionFacts.TryParse(asm.LangVersion, out var lv)) langVersion = lv;
            var parseOptions = new CSharpParseOptions(langVersion, preprocessorSymbols: asm.Defines);
            solution = solution.AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), asm.Name, asm.Name, LanguageNames.CSharp,
                parseOptions: parseOptions,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: asm.References.Where(File.Exists).Select(r => (MetadataReference)MetadataReference.CreateFromFile(r))));
            foreach (var src in asm.SourceFiles.Where(File.Exists))
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId), Path.GetFileName(src), SourceText.From(File.ReadAllText(src)), filePath: src);
        }
        return solution;
    }
}
```

(Add `using Microsoft.CodeAnalysis.Text;` for `SourceText`.)

- [ ] **Step 4: Implement `Commands.LoadModelAsync`** (in `Commands.cs`)

```csharp
public static async Task<object> LoadModelAsync(RpcServer server, JsonElement pars)
{
    var modelJson = pars.GetProperty("modelJson").GetString() ?? throw new RpcException("BAD_PARAMS", "modelJson required");
    var solution = WorkspaceBuilder.Build(modelJson);
    server.SetSolution(solution);
    // touch compilations so first real query is warm + surfaces load errors early
    foreach (var p in solution.Projects) await p.GetCompilationAsync();
    return new { loaded = true, projects = solution.Projects.Count() };
}
```

- [ ] **Step 5: Run it to verify it passes**

Run: `cd dotnet/UnityEditorMCP.Roslyn.Tests && dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add dotnet/UnityEditorMCP.Roslyn dotnet/UnityEditorMCP.Roslyn.Tests
git commit -m "feat(0.6.0): sidecar workspace builder — model.json -> Roslyn Solution (Plan 3)"
```

---

## Task 3–7: the semantic commands (one task each — same shape)

Each command is: **write the failing xUnit test over the Task-2 fixture → implement in `Commands.cs` → `dotnet test` → commit.** Each takes `(Solution? solution, JsonElement pars)` and throws `RpcException("NO_MODEL", …)` when `solution` is null. The implementations:

- [ ] **Task 3 — `find_references`** (`SymbolFinder.FindReferencesAsync`). Params `{name}` (resolve the symbol by name across the solution) or `{path, position}`. Test: references to `Health` include its declaration + the `Health--` use. Result `{ resolution: "semantic", refs: [{path, line, column}] }`.

```csharp
public static async Task<object> FindReferencesAsync(Solution? solution, JsonElement pars)
{
    if (solution == null) throw new RpcException("NO_MODEL", "call load_model first");
    var name = pars.GetProperty("name").GetString()!;
    var refs = new List<object>();
    foreach (var project in solution.Projects)
    {
        var comp = await project.GetCompilationAsync();
        if (comp == null) continue;
        foreach (var sym in comp.GetSymbolsWithName(name, SymbolFilter.Member | SymbolFilter.Type))
        {
            foreach (var r in await SymbolFinder.FindReferencesAsync(sym, solution))
                foreach (var loc in r.Locations)
                {
                    var span = loc.Location.GetLineSpan();
                    refs.Add(new { path = span.Path, line = span.StartLinePosition.Line + 1, column = span.StartLinePosition.Character + 1 });
                }
        }
    }
    return new { resolution = "semantic", count = refs.Count, refs };
}
```

- [ ] **Task 4 — `goto_definition`** (`SemanticModel.GetSymbolInfo` at a position → `symbol.Locations`). Params `{path, position:{line,column}}`. Result `{definition:{path,line,column,symbol}}` (or `null`).
- [ ] **Task 5 — `get_diagnostics`** (`Compilation.GetDiagnostics`). Params `{path?}`. Result `{diagnostics:[{severity,id,message,path,line}]}` (filter to `>= Warning`).
- [ ] **Task 6 — `rename_symbol`** (`Renamer.RenameSymbolAsync`). Params `{path, position, newName, dryRun?}`. Compute the renamed solution, **diff the changed documents** into `{edits:[{path, newText}]}`; only when `dryRun` is false write the files. Result `{edits, applied}`.
- [ ] **Task 7 — `get_type_hierarchy`** (`symbol.BaseType` chain, `symbol.AllInterfaces`, `SymbolFinder.FindDerivedClassesAsync`). Params `{typeName}`. Result `{base:[], derived:[], interfaces:[]}`.

Each: `cd dotnet/UnityEditorMCP.Roslyn.Tests && dotnet test --filter <Name>` then commit `feat(0.6.0): sidecar <command> (Plan 3)`.

---

## Task 8: Editor-side model export (`export_roslyn_model`)

**Files:**
- Create: `unity-editor-mcp/Editor/Handlers/RoslynModelExporter.cs`
- Modify: `unity-editor-mcp/Editor/Core/UnityEditorMCP.cs` (register), `protocol/catalog/commands.json` (+ regen)
- Create: `unity-editor-mcp/Tests/Editor/Handlers/RoslynModelExporterTests.cs`

- [ ] **Step 1: Write the failing NUnit test**

```csharp
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    public class RoslynModelExporterTests
    {
        [Test]
        public void Export_WritesModelFileWithAssembliesAndGeneration()
        {
            var outcome = RoslynModelExporter.Export(new JObject());
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            Assert.IsTrue(System.IO.File.Exists((string)data["modelPath"]));
            var model = JObject.Parse(System.IO.File.ReadAllText((string)data["modelPath"]));
            Assert.GreaterOrEqual(((JArray)model["assemblies"]).Count, 1);
            Assert.IsNotNull(model["generation"]);
        }
    }
}
```

- [ ] **Step 2: Verify it fails** — `refresh_assets` via the bridge → `read-editor-log.mjs` shows `RoslynModelExporter` undefined.

- [ ] **Step 3: Implement `RoslynModelExporter.cs`** (floor-safe: `CompilationPipeline` + `System.IO` + Newtonsoft only)

```csharp
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>Exports Unity's real CompilationPipeline model to Library/UnityEditorMCP/roslyn-model.json
    /// for the out-of-process Roslyn sidecar (ADR 0006). Floor-safe (CompilationPipeline is 2017.3+).</summary>
    public static class RoslynModelExporter
    {
        public static HandlerOutcome Export(JObject p)
        {
            try
            {
                var projectRoot = Path.GetFullPath(Application.dataPath + "/..");
                var dir = Path.Combine(projectRoot, "Library", "UnityEditorMCP");
                Directory.CreateDirectory(dir);
                var modelPath = Path.Combine(dir, "roslyn-model.json");

                var assemblies = new JArray();
                foreach (var asm in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
                {
                    var refs = new JArray();
                    foreach (var r in asm.allReferences) refs.Add(r);
                    var defines = new JArray();
                    foreach (var d in asm.defines) defines.Add(d);
                    var sources = new JArray();
                    foreach (var s in asm.sourceFiles) sources.Add(Path.GetFullPath(s));
                    assemblies.Add(new JObject {
                        ["name"] = asm.name, ["sourceFiles"] = sources, ["references"] = refs,
                        ["defines"] = defines, ["langVersion"] = "8.0" });
                }
                var generation = System.DateTime.UtcNow.Ticks;
                var model = new JObject { ["generation"] = generation, ["assemblies"] = assemblies };
                File.WriteAllText(modelPath, model.ToString());
                return HandlerOutcome.Ok(new JObject { ["modelPath"] = modelPath, ["generation"] = generation, ["assemblies"] = assemblies.Count });
            }
            catch (System.Exception e) { return HandlerOutcome.Fail($"Failed to export Roslyn model: {e.Message}"); }
        }
    }
}
```

- [ ] **Step 4: Register + catalog**

Add to `BuildDispatcher` (`UnityEditorMCP.cs`): `dispatcher.Register("export_roslyn_model", RoslynModelExporter.Export);`
Add an `export_roslyn_model` entry to `protocol/catalog/commands.json` (`sides:["editor"]`, category `code`, empty params, result `{modelPath, generation, assemblies}`). Then `node protocol/scripts/generate-csharp-catalog.mjs` and `node protocol/scripts/check-drift.mjs` (expect OK once the dispatch case exists).

- [ ] **Step 5: Recompile + run via the bridge** — `refresh_assets` → `read-editor-log.mjs` (clean) → `run_tests EditMode` → `get_test_results`; the new NUnit test passes. Then live-smoke `call_unity_tool(7093, export_roslyn_model)` and confirm `roslyn-model.json` is written with the project's assemblies.

- [ ] **Step 6: Commit** `feat(0.6.0): editor exports the CompilationPipeline model for the sidecar (Plan 3)`.

---

## Task 9: Node sidecar client + lazy download + wire the factory

**Files:**
- Create: `mcp-server/src/core/roslynSidecar.js`
- Modify: `mcp-server/src/core/roslynManager.js` (default factory → the real one)
- Create: `mcp-server/tests/unit/core/roslynSidecar.test.js`
- Modify: `mcp-server/package.json` (test:ci)

- [ ] **Step 1: Write the failing test** (protocol framing + download-guard against a stub child)

`roslynSidecar.test.js` exercises `RoslynSidecarClient` with an injected fake child process (a `{ stdin, stdout, stderr, kill }` with a writable/readable pair) — assert a `call('ping', {})` round-trips a `{id,method,params}` line and resolves the matching `{id,result}`; assert `dispose()` kills the child; assert two concurrent calls correlate by id.

- [ ] **Step 2: Implement `roslynSidecar.js`** — `RoslynSidecarClient` (newline-JSON framing over a child's stdio, an id→pending-promise map, 30s timeout, `dispose()`), `ensureBinary()` (resolve the cached path under `os.homedir()/.cache/unity-editor-mcp-roslyn/<version>/`; if absent, download the platform asset from the public GitHub release using a `manifest.json` for the URL + SHA-256, verify, chmod +x; return null on any failure → caller maps to `unavailable`), and `makeRoslynClientFactory({ exporter })` returning `async (conn) => { ... }` that: calls `export_roslyn_model` on the editor (via `conn.sendCommand`), `ensureBinary()`, spawns it, `call('load_model', { modelJson })`, returns the client (or null → unavailable).

- [ ] **Step 3: Wire the factory** — in `roslynManager.js`, change the singleton to use the real factory: `export const roslynManager = new RoslynManager(makeRoslynClientFactory());`. Keep the `RoslynManager` class factory-injectable (tests still pass fakes). The default factory now attempts the real path; with no binary/network it returns null → `unavailable` (Plan 2's contract holds).

- [ ] **Step 4: Run tests** — `node --test tests/unit/core/roslynSidecar.test.js`; add it to `test:ci`; `npm run test:ci`. Expected: all pass (the manager/tools tests are unaffected — they inject fakes).

- [ ] **Step 5: Commit** `feat(0.6.0): Node Roslyn sidecar client + lazy download + factory wiring (Plan 3)`.

---

## Task 10: Release pipeline (build + publish the binaries)

**Files:** Create `.github/workflows/roslyn-sidecar-release.yml`

- [ ] **Step 1: Author the workflow** — on a `roslyn-v*` tag, a matrix (`win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`) runs `dotnet publish dotnet/UnityEditorMCP.Roslyn -c Release -r <rid> --self-contained -p:PublishSingleFile=true`, computes SHA-256, and uploads each binary + a generated `manifest.json` (`{version, assets:{<rid>:{url, sha256}}}`) as **release assets** (free on the public repo). A final job assembles `manifest.json` from the per-RID hashes.

- [ ] **Step 2: Document** — add a short "Roslyn backend (optional)" section to `mcp-server/README.md`: it's opt-in, downloaded on first `start_roslyn`, requires no local .NET, and is removable (delete the cache dir). Note the base install ships no .NET.

- [ ] **Step 3: Commit** `ci(0.6.0): build + release the self-contained Roslyn sidecar binaries (Plan 3)`.

(The workflow runs only when the maintainer pushes a `roslyn-v*` tag — a release action, consistent with the "maintainer ships" boundary. The Node client tolerates a missing release: `ensureBinary()` → null → `unavailable`.)

---

## Task 11: End-to-end verification (live, on the floor)

**Files:** none (verification)

- [ ] **Step 1:** With a built local binary (point the cache at a local `dotnet publish` output via an env override, or place it in the cache dir), drive the full path on the live 2020.3 editor: `call_unity_tool(7093, start_roslyn)` → `roslyn_status` until `ready` → `call_unity_tool(7093, find_references, {name:"<a real project symbol>"})` and confirm `resolution:"semantic"` with real locations; `get_diagnostics`; `rename_symbol {dryRun:true}` returns an edit set without writing.
- [ ] **Step 2:** Confirm graceful degradation: with no binary in the cache + no network, `start_roslyn` → `unavailable`, and `find_references` (no sidecar) still returns `resolution:"syntactic"` from the editor (lite). Gates: `test:ci`, `dotnet test` (both Core + the new Roslyn project), `check-drift` (export_roslyn_model is the only new catalog entry), editor EditMode green.
- [ ] **Step 3: Commit** `chore(0.6.0): Roslyn sidecar — end-to-end floor verification (Plan 3)`.

---

## Self-review

- **Spec coverage:** implements spec §2 Layer 2 (the sidecar, Workspaces, the temp-file model, error-tolerant load via `GetDiagnostics`), §4 (the five semantic commands), §5 (lazy-download distribution + provenance via the `manifest.json` SHA-256; `start_roslyn` → `unavailable` when absent), and closes Plan 2's seam (the real client factory replaces the `null` default; `RoslynManager` unchanged). `rename_symbol` is dry-run-first + diffed (the spec's atomic-apply intent).
- **Type/contract consistency:** the RPC method names (`load_model`/`find_references`/`goto_definition`/`get_diagnostics`/`rename_symbol`/`get_type_hierarchy`) match `roslynTools`' gated set + the `roslynSidecar` client `.call(tool, params)`; `find_references` returns `resolution:"semantic"` (matches Plan 1's `resolution` contract); the model JSON shape is identical in `RoslynModelExporter` (writer) and `WorkspaceBuilder`/`ProjectModel` (reader).
- **Floor safety:** the editor exporter uses only `CompilationPipeline` (2017.3+) + `System.IO` + Newtonsoft — C# 8 / 2020.3-clean (verify via `compat-lint` + the EditMode run). The sidecar is out-of-process net8 — not floor-coupled. The base npm install gains no .NET dependency.
- **No placeholders:** Tasks 1, 2, 8, 9 carry full code; Tasks 3–7 give the exact Roslyn API per command + the one fully-worked `find_references` body as the pattern; Tasks 10–11 are concrete commands/workflow + the live dogfly checklist.
- **Assumptions to verify at execution:** (a) `CompilationPipeline.Assembly.allReferences` returns absolute dll paths on 2020.3 (confirmed by the Plan-2 design review) — if relative, `Path.GetFullPath` them in the exporter; (b) `LanguageVersionFacts.TryParse` accepts `"8.0"` (else map manually); (c) the self-contained single-file size (~30–70 MB) is well under the 2 GiB release-asset limit.
