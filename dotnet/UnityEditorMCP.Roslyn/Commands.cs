using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace UnityEditorMCP.Roslyn;

/// <summary>The semantic commands over a loaded <see cref="Solution"/>. Tasks 2–7 fill these in;
/// they start as NOT_IMPLEMENTED stubs so the RPC dispatch in <see cref="RpcServer"/> compiles.</summary>
public static partial class Commands
{
    public static async Task<object> LoadModelAsync(RpcServer server, JsonElement pars)
    {
        var modelJson = pars.GetProperty("modelJson").GetString() ?? throw new RpcException("BAD_PARAMS", "modelJson required");
        var solution = WorkspaceBuilder.Build(modelJson);
        server.SetSolution(solution);
        // touch compilations so the first real query is warm + load errors surface early
        foreach (var p in solution.Projects) await p.GetCompilationAsync();
        return new { loaded = true, projects = solution.Projects.Count() };
    }
    public static Task<object> FindReferencesAsync(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "find_references");
    public static Task<object> GotoDefinitionAsync(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "goto_definition");
    public static object GetDiagnostics(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "get_diagnostics");
    public static Task<object> RenameSymbolAsync(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "rename_symbol");
    public static Task<object> GetTypeHierarchyAsync(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "get_type_hierarchy");
}
