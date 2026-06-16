using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

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
        var seen = new HashSet<string>();
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
                        int line = span.StartLinePosition.Line + 1, col = span.StartLinePosition.Character + 1;
                        if (seen.Add($"{span.Path}:{line}:{col}")) // a symbol surfaces in every project that references it — dedupe
                            refs.Add(new { path = span.Path, line, column = col });
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

        if (!SyntaxFacts.IsValidIdentifier(newName))
            throw new RpcException("BAD_PARAMS", $"'{newName}' is not a valid C# identifier");

        var (_, symbol) = await ResolveSymbolAt(solution, path, pos.GetProperty("line").GetInt32(), pos.GetProperty("column").GetInt32());
        if (symbol == null) throw new RpcException("NO_SYMBOL", "no renameable symbol at the position");
        if (!symbol.Locations.Any(l => l.IsInSource))
            throw new RpcException("NO_SYMBOL", "symbol is defined in metadata (not source) — cannot rename");

        var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName);

        var edits = new List<object>();
        var writes = new List<(string path, SourceText text)>();
        foreach (var projChange in newSolution.GetChanges(solution).GetProjectChanges())
            foreach (var docId in projChange.GetChangedDocuments())
            {
                var newDoc = newSolution.GetDocument(docId)!;
                var sourceText = await newDoc.GetTextAsync();
                edits.Add(new { path = newDoc.FilePath ?? newDoc.Name, newText = sourceText.ToString() });
                if (newDoc.FilePath != null) writes.Add((newDoc.FilePath, sourceText)); // bounded to loaded model sources
            }

        if (!dryRun) WriteEditsAtomically(writes);
        return new { applied = !dryRun, count = edits.Count, edits };
    }

    // Write renamed documents preserving each file's detected encoding (BOM-aware), staging to temp files
    // first so a disk/permission failure modifies NO originals; then replacing atomically per file.
    private static void WriteEditsAtomically(List<(string path, SourceText text)> writes)
    {
        var staged = new List<(string tmp, string dest)>();
        try
        {
            foreach (var (dest, text) in writes)
            {
                var tmp = dest + ".uemcp.tmp";
                File.WriteAllText(tmp, text.ToString(), text.Encoding ?? new UTF8Encoding(false));
                staged.Add((tmp, dest));
            }
        }
        catch
        {
            foreach (var (tmp, _) in staged) { try { File.Delete(tmp); } catch { /* ignore */ } }
            throw new RpcException("WRITE_FAILED", "failed to stage renamed files; no originals were modified");
        }
        foreach (var (tmp, dest) in staged)
        {
            if (File.Exists(dest)) File.Replace(tmp, dest, null);
            else File.Move(tmp, dest);
        }
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
        if (line < 1 || col < 1) return (null, null); // 1-indexed; reject 0/negative before indexing
        foreach (var project in solution.Projects)
        {
            var doc = project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (doc == null) continue;
            var text = await doc.GetTextAsync();
            if (line - 1 >= text.Lines.Count) continue;
            var lineSpan = text.Lines[line - 1];
            var position = lineSpan.Start + (col - 1);
            if (position > lineSpan.End) continue; // column past end of line → not a valid position
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
