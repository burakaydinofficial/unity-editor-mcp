using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEditorMCP.Roslyn;
using Xunit;

public class CommandsTests
{
    const string Source =
        "namespace G\n" +
        "{\n" +
        "    public interface IDamageable { void Hit(); }\n" +
        "    public class Player : IDamageable\n" +
        "    {\n" +
        "        public int Health;\n" +
        "        public void Hit() { Health--; }\n" +
        "    }\n" +
        "    public class Boss : Player { }\n" +
        "}\n";

    static readonly string[] RuntimeRefs = new[] { typeof(object).Assembly.Location };

    static (string json, string srcPath) WriteModel(string source, string[]? refs = null)
    {
        var dir = Directory.CreateTempSubdirectory("roslyn-cmd").FullName;
        var srcPath = Path.Combine(dir, "Code.cs");
        File.WriteAllText(srcPath, source);
        var model = new { generation = 1, assemblies = new[] { new {
            name = "Assembly-CSharp", sourceFiles = new[] { srcPath },
            references = refs ?? RuntimeRefs, defines = Array.Empty<string>(), langVersion = "8.0" } } };
        return (JsonSerializer.Serialize(model), srcPath);
    }

    static JsonElement Pars(object o) => JsonSerializer.SerializeToElement(o);
    static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result);

    static (int line, int col) PosOf(string src, string needle, int occurrence)
    {
        int idx = -1;
        for (int i = 0; i < occurrence; i++) idx = src.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        int line = 1, col = 1;
        for (int i = 0; i < idx; i++) { if (src[i] == '\n') { line++; col = 1; } else col++; }
        return (line, col);
    }

    [Fact]
    public async Task FindReferences_FindsHealthUse()
    {
        var (json, _) = WriteModel(Source);
        var sol = WorkspaceBuilder.Build(json);
        var res = Json(await Commands.FindReferencesAsync(sol, Pars(new { name = "Health" })));
        Assert.Equal("semantic", res.GetProperty("resolution").GetString());
        Assert.True(res.GetProperty("count").GetInt32() >= 1);
    }

    [Fact]
    public async Task GotoDefinition_FromUse_LandsOnDeclaration()
    {
        var (json, srcPath) = WriteModel(Source);
        var sol = WorkspaceBuilder.Build(json);
        var declLine = PosOf(Source, "Health", 1).line;     // the field declaration
        var (useLine, useCol) = PosOf(Source, "Health", 2);  // the Health-- use
        var res = Json(await Commands.GotoDefinitionAsync(sol, Pars(new { path = srcPath, position = new { line = useLine, column = useCol } })));
        var def = res.GetProperty("definition");
        Assert.NotEqual(JsonValueKind.Null, def.ValueKind);
        Assert.Equal(declLine, def.GetProperty("line").GetInt32());
    }

    [Fact]
    public void GetDiagnostics_ReportsAnError()
    {
        var (json, _) = WriteModel("namespace G { public class Bad { void M() { Undefined(); } } }");
        var sol = WorkspaceBuilder.Build(json);
        var res = Json(Commands.GetDiagnostics(sol, Pars(new { })));
        Assert.True(res.GetProperty("count").GetInt32() >= 1);
        Assert.Contains(res.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("id").GetString()), id => id == "CS0103");
    }

    [Fact]
    public async Task RenameSymbol_DryRun_ReturnsEditsWithoutWriting()
    {
        var (json, srcPath) = WriteModel(Source);
        var sol = WorkspaceBuilder.Build(json);
        var before = File.ReadAllText(srcPath);
        var (l, c) = PosOf(Source, "Health", 1); // the field declaration
        var res = Json(await Commands.RenameSymbolAsync(sol, Pars(new { path = srcPath, position = new { line = l, column = c }, newName = "Hp", dryRun = true })));
        Assert.False(res.GetProperty("applied").GetBoolean());
        Assert.True(res.GetProperty("edits").GetArrayLength() >= 1);
        Assert.Equal(before, File.ReadAllText(srcPath)); // unchanged on disk (dry run)
        var newText = res.GetProperty("edits")[0].GetProperty("newText").GetString()!;
        Assert.Contains("Hp", newText);
        Assert.DoesNotContain("Health", newText);
    }

    [Fact]
    public async Task GetTypeHierarchy_ReturnsDerivedAndInterfaces()
    {
        var (json, _) = WriteModel(Source);
        var sol = WorkspaceBuilder.Build(json);
        var res = Json(await Commands.GetTypeHierarchyAsync(sol, Pars(new { typeName = "Player" })));
        Assert.Contains(res.GetProperty("derived").EnumerateArray().Select(d => d.GetString()), s => s!.Contains("Boss"));
        Assert.Contains(res.GetProperty("interfaces").EnumerateArray().Select(d => d.GetString()), s => s!.Contains("IDamageable"));
    }
}
