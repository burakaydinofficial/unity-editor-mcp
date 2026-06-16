using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace UnityEditorMCP.Roslyn;

/// <summary>The semantic commands over a loaded <see cref="Solution"/>. Tasks 2–7 fill these in;
/// they start as NOT_IMPLEMENTED stubs so the RPC dispatch in <see cref="RpcServer"/> compiles.</summary>
public static partial class Commands
{
    public static Task<object> LoadModelAsync(RpcServer server, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "load_model");
    public static Task<object> FindReferencesAsync(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "find_references");
    public static Task<object> GotoDefinitionAsync(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "goto_definition");
    public static object GetDiagnostics(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "get_diagnostics");
    public static Task<object> RenameSymbolAsync(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "rename_symbol");
    public static Task<object> GetTypeHierarchyAsync(Solution? solution, JsonElement pars) => throw new RpcException("NOT_IMPLEMENTED", "get_type_hierarchy");
}
