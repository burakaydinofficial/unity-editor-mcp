using System;
using System.IO;
using System.Linq;
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
        var solution = WorkspaceBuilder.Build(json);
        Assert.Single(solution.Projects);
        var comp = solution.Projects.First().GetCompilationAsync().GetAwaiter().GetResult()!;
        var type = comp.GetTypeByMetadataName("G.Player");
        Assert.NotNull(type);
    }
}
