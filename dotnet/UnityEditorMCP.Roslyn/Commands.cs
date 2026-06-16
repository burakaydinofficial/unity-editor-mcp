using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace UnityEditorMCP.Roslyn;

/// <summary>The semantic commands over a loaded <see cref="Solution"/> (ADR 0006). Each throws
/// NO_MODEL until load_model has run.</summary>
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

    public static async Task<object> GotoDefinitionAsync(Solution? solution, JsonElement pars)
    {
        if (solution == null) throw new RpcException("NO_MODEL", "call load_model first");
        var path = pars.GetProperty("path").GetString()!;
        var pos = pars.GetProperty("position");
        var (_, symbol) = await ResolveSymbolAt(solution, path, pos.GetProperty("line").GetInt32(), pos.GetProperty("column").GetInt32());
        if (symbol == null) return new { definition = (object?)null };
        var defLoc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (defLoc == null) return new { definition = (object?)null };
        var span = defLoc.GetLineSpan();
        return new { definition = new { path = span.Path, line = span.StartLinePosition.Line + 1, column = span.StartLinePosition.Character + 1, symbol = symbol.ToDisplayString() } };
    }

    public static object GetDiagnostics(Solution? solution, JsonElement pars)
    {
        if (solution == null) throw new RpcException("NO_MODEL", "call load_model first");
        var diags = new List<object>();
        foreach (var project in solution.Projects)
        {
            var comp = project.GetCompilationAsync().GetAwaiter().GetResult();
            if (comp == null) continue;
            foreach (var d in comp.GetDiagnostics())
            {
                if (d.Severity < DiagnosticSeverity.Warning) continue;
                // CS1701/CS1702 are assembly version binding-redirect warnings — pure noise from the
                // reconstructed Unity reference set (engine dlls bind to different System.* versions than the
                // netstandard facade). They dominate (~96%) and never reflect a user-code issue, so drop them.
                if (d.Id == "CS1701" || d.Id == "CS1702") continue;
                var span = d.Location.GetLineSpan();
                diags.Add(new { severity = d.Severity.ToString(), id = d.Id, message = d.GetMessage(), path = span.Path, line = span.StartLinePosition.Line + 1 });
            }
        }
        return new { count = diags.Count, diagnostics = diags };
    }

    public static async Task<object> RenameSymbolAsync(Solution? solution, JsonElement pars)
    {
        if (solution == null) throw new RpcException("NO_MODEL", "call load_model first");
        var path = pars.GetProperty("path").GetString()!;
        var pos = pars.GetProperty("position");
        var newName = pars.GetProperty("newName").GetString()!;
        var dryRun = pars.TryGetProperty("dryRun", out var dr) && dr.GetBoolean();

        var (_, symbol) = await ResolveSymbolAt(solution, path, pos.GetProperty("line").GetInt32(), pos.GetProperty("column").GetInt32());
        if (symbol == null) throw new RpcException("NO_SYMBOL", "no renameable symbol at the position");

        var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName);

        var edits = new List<object>();
        var changed = new List<(string path, string text)>();
        foreach (var projChange in newSolution.GetChanges(solution).GetProjectChanges())
            foreach (var docId in projChange.GetChangedDocuments())
            {
                var newDoc = newSolution.GetDocument(docId)!;
                var text = (await newDoc.GetTextAsync()).ToString();
                edits.Add(new { path = newDoc.FilePath ?? newDoc.Name, newText = text });
                if (newDoc.FilePath != null) changed.Add((newDoc.FilePath, text));
            }

        if (!dryRun) foreach (var (p, t) in changed) File.WriteAllText(p, t);
        return new { applied = !dryRun, count = edits.Count, edits };
    }

    public static async Task<object> GetTypeHierarchyAsync(Solution? solution, JsonElement pars)
    {
        if (solution == null) throw new RpcException("NO_MODEL", "call load_model first");
        var typeName = pars.GetProperty("typeName").GetString()!;
        foreach (var project in solution.Projects)
        {
            var comp = await project.GetCompilationAsync();
            if (comp == null) continue;
            var type = comp.GetSymbolsWithName(typeName, SymbolFilter.Type).OfType<INamedTypeSymbol>().FirstOrDefault();
            if (type == null) continue;
            var bases = new List<string>();
            for (var b = type.BaseType; b != null; b = b.BaseType) bases.Add(b.ToDisplayString());
            var ifaces = type.AllInterfaces.Select(i => i.ToDisplayString()).ToList();
            var derived = (await SymbolFinder.FindDerivedClassesAsync(type, solution)).Select(d => d.ToDisplayString()).ToList();
            return new { type = type.ToDisplayString(), @base = bases, derived, interfaces = ifaces };
        }
        throw new RpcException("NOT_FOUND", $"type not found: {typeName}");
    }

    // Resolve the symbol referenced (or declared) at a 1-indexed line/column in a document.
    private static async Task<(Document? doc, ISymbol? symbol)> ResolveSymbolAt(Solution solution, string path, int line, int col)
    {
        foreach (var project in solution.Projects)
        {
            var doc = project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (doc == null) continue;
            var text = await doc.GetTextAsync();
            if (line - 1 >= text.Lines.Count) continue;
            var position = text.Lines[line - 1].Start + (col - 1);
            var model = await doc.GetSemanticModelAsync();
            var root = await doc.GetSyntaxRootAsync();
            if (model == null || root == null) continue;
            var node = root.FindToken(position).Parent;
            if (node == null) continue;
            var symbol = model.GetSymbolInfo(node).Symbol ?? model.GetDeclaredSymbol(node);
            if (symbol != null) return (doc, symbol);
        }
        return (null, null);
    }
}
