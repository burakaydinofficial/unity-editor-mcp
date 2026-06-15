using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Syntactic C# code intelligence over the project's Assets scripts: file outline (get_symbols),
    /// symbol lookup (find_symbol), textual references (find_references), and symbol-body extraction
    /// (get_symbol_body).
    ///
    /// This is a SYNTACTIC analyzer: it first masks comments and string/char literals (replacing their
    /// content with spaces so they never produce false matches), then scans the masked text with
    /// regexes + brace matching. It does NOT do semantic resolution — "references" are textual
    /// identifier matches, and same-named members across types/overloads are not disambiguated. It is
    /// intentionally dependency-free and floor-safe (no Roslyn, no external LSP); a full semantic layer
    /// is a separate opt-in milestone. find_symbol/find_references scan `Assets/`; get_symbols and
    /// get_symbol_body accept any .cs path under the project.
    /// </summary>
    public static class CodeIntelligenceHandler
    {
        private const int DefaultMaxResults = 200;

        // ---- public operations ----

        public static JObject GetSymbols(JObject p)
        {
            try
            {
                var path = p["path"]?.ToString();
                if (string.IsNullOrEmpty(path)) return Err("Missing required parameter: path");
                var full = ResolveScript(path);
                if (full == null) return Err($"Not a .cs path: {path}");
                if (!File.Exists(full)) return Err($"File not found: {path}");

                var src = File.ReadAllText(full);
                var symbols = Extract(src);
                var arr = new JArray();
                foreach (var s in symbols) arr.Add(s.ToJson());
                return new JObject { ["path"] = Rel(full), ["count"] = symbols.Count, ["symbols"] = arr };
            }
            catch (Exception e) { return Err($"Error reading symbols: {e.Message}"); }
        }

        public static JObject FindSymbol(JObject p)
        {
            try
            {
                var name = p["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) return Err("Missing required parameter: name");
                var kindFilter = p["kind"]?.ToString();
                var max = p["maxResults"]?.ToObject<int?>() ?? DefaultMaxResults;

                var matches = new JArray();
                int total = 0;
                foreach (var file in EnumerateScripts())
                {
                    var src = File.ReadAllText(file);
                    foreach (var s in Extract(src))
                    {
                        if (s.Name != name) continue;
                        if (!string.IsNullOrEmpty(kindFilter) && s.Kind != kindFilter) continue;
                        total++;
                        if (matches.Count < max)
                        {
                            var j = s.ToJson();
                            j["file"] = Rel(file);
                            matches.Add(j);
                        }
                    }
                }
                return new JObject
                {
                    ["name"] = name,
                    ["count"] = total,
                    ["truncated"] = total > matches.Count,
                    ["matches"] = matches
                };
            }
            catch (Exception e) { return Err($"Error finding symbol: {e.Message}"); }
        }

        public static JObject FindReferences(JObject p)
        {
            try
            {
                var name = p["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) return Err("Missing required parameter: name");
                var max = p["maxResults"]?.ToObject<int?>() ?? DefaultMaxResults;
                var ident = new Regex(@"\b" + Regex.Escape(name) + @"\b");

                var refs = new JArray();
                int total = 0;
                foreach (var file in EnumerateScripts())
                {
                    var src = File.ReadAllText(file);
                    var masked = Mask(src);
                    var lineStarts = BuildLineIndex(src);
                    foreach (Match m in ident.Matches(masked))
                    {
                        total++;
                        if (refs.Count < max)
                        {
                            int ln = LineNumberAt(lineStarts, m.Index);
                            refs.Add(new JObject
                            {
                                ["file"] = Rel(file),
                                ["line"] = ln,
                                ["text"] = LineText(src, lineStarts, ln).Trim()
                            });
                        }
                    }
                }
                return new JObject
                {
                    ["name"] = name,
                    ["count"] = total,
                    ["truncated"] = total > refs.Count,
                    ["references"] = refs,
                    ["note"] = "Textual (syntactic) identifier matches in code; comments and string literals are excluded, but there is no semantic resolution (same-named members across types/overloads are not disambiguated)."
                };
            }
            catch (Exception e) { return Err($"Error finding references: {e.Message}"); }
        }

        public static JObject GetSymbolBody(JObject p)
        {
            try
            {
                var path = p["path"]?.ToString();
                var name = p["name"]?.ToString();
                if (string.IsNullOrEmpty(path)) return Err("Missing required parameter: path");
                if (string.IsNullOrEmpty(name)) return Err("Missing required parameter: name");
                var full = ResolveScript(path);
                if (full == null || !File.Exists(full)) return Err($"File not found: {path}");

                var src = File.ReadAllText(full);
                var sym = Extract(src).FirstOrDefault(s => s.Name == name);
                if (sym == null) return Err($"Symbol '{name}' not found in {path}");

                var lines = src.Replace("\r\n", "\n").Split('\n');
                int start = Math.Max(0, sym.Line - 1);
                int end = Math.Min(sym.EndLine, lines.Length) - 1;
                var body = string.Join("\n", lines.Skip(start).Take(end - start + 1));
                return new JObject
                {
                    ["path"] = Rel(full),
                    ["name"] = name,
                    ["kind"] = sym.Kind,
                    ["startLine"] = sym.Line,
                    ["endLine"] = sym.EndLine,
                    ["source"] = body
                };
            }
            catch (Exception e) { return Err($"Error getting symbol body: {e.Message}"); }
        }

        // ---- core extraction ----

        private class Sym
        {
            public string Kind; public string Name; public int Line; public int EndLine; public string Signature;
            public JObject ToJson() => new JObject
            {
                ["kind"] = Kind, ["name"] = Name, ["line"] = Line, ["endLine"] = EndLine, ["signature"] = Signature
            };
        }

        private static readonly Regex TypeRe = new Regex(@"\b(class|struct|interface|enum)\s+([A-Za-z_]\w*)");
        // method/constructor: name (...) followed by a body opener. Deliberately omits the ';' terminator
        // so that ordinary calls ("DoThing();") are not mistaken for abstract declarations.
        private static readonly Regex MethodRe = new Regex(@"([A-Za-z_]\w*)\s*(?:<[^>(]*>)?\s*\([^;{)]*\)\s*(\{|=>|where\b)");
        private static readonly Regex PropRe = new Regex(@"\b[A-Za-z_][\w<>\[\].,]*\s+([A-Za-z_]\w*)\s*\{\s*(?:get|set)\b");
        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "if","for","foreach","while","switch","catch","using","lock","fixed","return","new","sizeof",
            "typeof","nameof","default","do","else","get","set","add","remove","yield","await","throw","when"
        };

        private static List<Sym> Extract(string src)
        {
            var masked = Mask(src);
            var lineStarts = BuildLineIndex(src);
            var result = new List<Sym>();

            foreach (Match m in TypeRe.Matches(masked))
            {
                int line = LineNumberAt(lineStarts, m.Index);
                int end = MatchBlockEnd(masked, lineStarts, m.Index, line);
                result.Add(new Sym { Kind = m.Groups[1].Value, Name = m.Groups[2].Value, Line = line, EndLine = end, Signature = LineText(src, lineStarts, line).Trim() });
            }

            foreach (Match m in MethodRe.Matches(masked))
            {
                var name = m.Groups[1].Value;
                if (Keywords.Contains(name)) continue;
                // skip member-access calls ("obj.Method(")
                int b = m.Index - 1;
                while (b >= 0 && char.IsWhiteSpace(masked[b])) b--;
                if (b >= 0 && masked[b] == '.') continue;
                int line = LineNumberAt(lineStarts, m.Index);
                int end = m.Groups[2].Value == "{" ? MatchBlockEnd(masked, lineStarts, m.Index, line) : line;
                result.Add(new Sym { Kind = "method", Name = name, Line = line, EndLine = end, Signature = LineText(src, lineStarts, line).Trim() });
            }

            foreach (Match m in PropRe.Matches(masked))
            {
                var name = m.Groups[1].Value;
                if (Keywords.Contains(name)) continue;
                int line = LineNumberAt(lineStarts, m.Index);
                int end = MatchBlockEnd(masked, lineStarts, m.Index, line);
                result.Add(new Sym { Kind = "property", Name = name, Line = line, EndLine = end, Signature = LineText(src, lineStarts, line).Trim() });
            }

            return result.OrderBy(s => s.Line).ThenBy(s => s.Name).ToList();
        }

        // Line of the close-brace matching the first '{' at/after fromIndex (brace depth on masked text).
        private static int MatchBlockEnd(string masked, int[] lineStarts, int fromIndex, int declLine)
        {
            int i = masked.IndexOf('{', fromIndex);
            if (i < 0) return declLine;
            int depth = 0;
            for (; i < masked.Length; i++)
            {
                if (masked[i] == '{') depth++;
                else if (masked[i] == '}') { depth--; if (depth == 0) return LineNumberAt(lineStarts, i); }
            }
            return declLine;
        }

        // ---- masking: replace comment + string/char literal CONTENT with spaces (newlines preserved) ----

        private static string Mask(string s)
        {
            var a = s.ToCharArray();
            int n = a.Length, i = 0;
            while (i < n)
            {
                char c = a[i];
                if (c == '/' && i + 1 < n && a[i + 1] == '/')
                {
                    while (i < n && a[i] != '\n') { a[i] = ' '; i++; }
                    continue;
                }
                if (c == '/' && i + 1 < n && a[i + 1] == '*')
                {
                    a[i] = ' '; a[i + 1] = ' '; i += 2;
                    while (i < n && !(a[i] == '*' && i + 1 < n && a[i + 1] == '/'))
                    {
                        if (a[i] != '\n') a[i] = ' ';
                        i++;
                    }
                    if (i < n) { a[i] = ' '; if (i + 1 < n) a[i + 1] = ' '; i += 2; }
                    continue;
                }
                if (c == '@' && i + 1 < n && a[i + 1] == '"') // verbatim string
                {
                    a[i] = ' '; a[i + 1] = ' '; i += 2;
                    while (i < n)
                    {
                        if (a[i] == '"')
                        {
                            if (i + 1 < n && a[i + 1] == '"') { a[i] = ' '; a[i + 1] = ' '; i += 2; continue; }
                            a[i] = ' '; i++; break;
                        }
                        if (a[i] != '\n') a[i] = ' ';
                        i++;
                    }
                    continue;
                }
                if (c == '"') // regular (and interpolated $"...") string
                {
                    a[i] = ' '; i++;
                    while (i < n && a[i] != '"')
                    {
                        if (a[i] == '\\' && i + 1 < n) { a[i] = ' '; a[i + 1] = ' '; i += 2; continue; }
                        if (a[i] != '\n') a[i] = ' ';
                        i++;
                    }
                    if (i < n) { a[i] = ' '; i++; }
                    continue;
                }
                if (c == '\'') // char literal
                {
                    a[i] = ' '; i++;
                    while (i < n && a[i] != '\'')
                    {
                        if (a[i] == '\\' && i + 1 < n) { a[i] = ' '; a[i + 1] = ' '; i += 2; continue; }
                        a[i] = ' '; i++;
                    }
                    if (i < n) { a[i] = ' '; i++; }
                    continue;
                }
                i++;
            }
            return new string(a);
        }

        // ---- line index helpers ----

        private static int[] BuildLineIndex(string s)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < s.Length; i++) if (s[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        private static int LineNumberAt(int[] starts, int idx)
        {
            int lo = 0, hi = starts.Length - 1, ans = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (starts[mid] <= idx) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans + 1; // 1-based
        }

        private static string LineText(string src, int[] starts, int line)
        {
            int idx = line - 1;
            if (idx < 0 || idx >= starts.Length) return "";
            int start = starts[idx];
            int end = (idx + 1 < starts.Length) ? starts[idx + 1] - 1 : src.Length;
            if (end > start && src[end - 1] == '\r') end--;
            return src.Substring(start, Math.Max(0, end - start));
        }

        // ---- file resolution (project-scoped) ----

        private static IEnumerable<string> EnumerateScripts()
        {
            return Directory.EnumerateFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories);
        }

        private static string ResolveScript(string path)
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return null;
            string full;
            if (Path.IsPathRooted(path)) full = path;
            else
            {
                var root = Directory.GetParent(Application.dataPath)?.FullName;
                if (root == null) return null;
                full = Path.GetFullPath(Path.Combine(root, path));
            }
            return full.Replace("\\", "/");
        }

        private static string Rel(string full)
        {
            var root = Directory.GetParent(Application.dataPath)?.FullName?.Replace("\\", "/");
            full = full.Replace("\\", "/");
            return (root != null && full.StartsWith(root)) ? full.Substring(root.Length).TrimStart('/') : full;
        }

        private static JObject Err(string error) => new JObject { ["error"] = error };
    }
}
