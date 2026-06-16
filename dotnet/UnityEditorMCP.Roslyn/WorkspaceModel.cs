using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

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

/// <summary>Builds a Roslyn <see cref="Solution"/> from the editor-exported model (one project per Unity
/// assembly; sources as documents; reference dll paths as metadata references).</summary>
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
            var parseOptions = new CSharpParseOptions(langVersion, preprocessorSymbols: asm.Defines ?? Array.Empty<string>());
            solution = solution.AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), asm.Name, asm.Name, LanguageNames.CSharp,
                parseOptions: parseOptions,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: (asm.References ?? Array.Empty<string>()).Where(File.Exists).Select(r => (MetadataReference)MetadataReference.CreateFromFile(r))));
            foreach (var src in (asm.SourceFiles ?? Array.Empty<string>()).Where(File.Exists))
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId), Path.GetFileName(src), SourceText.From(File.ReadAllText(src)), filePath: src);
        }
        return solution;
    }
}
