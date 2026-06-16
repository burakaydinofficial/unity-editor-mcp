using System.Text.Json;

namespace UnityEditorMCP.Roslyn;

public static class Program
{
    public static async Task Main()
    {
        var rpc = new RpcServer();
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
    private Microsoft.CodeAnalysis.Solution? _solution; // set by load_model

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

    internal void SetSolution(Microsoft.CodeAnalysis.Solution s) => _solution = s;

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
