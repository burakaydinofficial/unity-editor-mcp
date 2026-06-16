using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

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
        private const int MaxResultsCeiling = 1000;
        private const long MaxFileBytes = 512 * 1024;

        // ---- public operations ----

        public static HandlerOutcome GetSymbols(JObject p)
        {
            try
            {
                var path = p["path"]?.ToString();
                if (string.IsNullOrEmpty(path)) return Err("Missing required parameter: path", "VALIDATION_ERROR");
                var full = ResolveScript(path);
                if (full == null) return Err($"Path is not a .cs file inside the project: {path}", "VALIDATION_ERROR");
                if (!File.Exists(full)) return Err($"File not found: {path}", "NOT_FOUND");

                var src = File.ReadAllText(full);
                var symbols = Extract(src);
                var arr = new JArray();
                foreach (var s in symbols) arr.Add(s.ToJson());
                return HandlerOutcome.Ok(new JObject { ["path"] = Rel(full), ["count"] = symbols.Count, ["symbols"] = arr });
            }
            catch (Exception e) { return Err($"Error reading symbols: {e.Message}"); }
        }

        public static HandlerOutcome FindSymbol(JObject p)
        {
            try
            {
                var name = p["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) return Err("Missing required parameter: name", "VALIDATION_ERROR");
                var kindFilter = p["kind"]?.ToString();
                var max = Math.Max(1, Math.Min(p["maxResults"]?.ToObject<int?>() ?? DefaultMaxResults, MaxResultsCeiling));

                var matches = new JArray();
                int total = 0;
                foreach (var file in EnumerateScripts())
                {
                    if (new FileInfo(file).Length > MaxFileBytes) continue;
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
                return HandlerOutcome.Ok(new JObject
                {
                    ["name"] = name,
                    ["count"] = total,
                    ["truncated"] = total > matches.Count,
                    ["matches"] = matches
                });
            }
            catch (Exception e) { return Err($"Error finding symbol: {e.Message}"); }
        }

        public static HandlerOutcome FindReferences(JObject p)
        {
            try
            {
                var name = p["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) return Err("Missing required parameter: name", "VALIDATION_ERROR");
                var max = Math.Max(1, Math.Min(p["maxResults"]?.ToObject<int?>() ?? DefaultMaxResults, MaxResultsCeiling));
                var ident = new Regex(@"\b" + Regex.Escape(name) + @"\b");

                var refs = new JArray();
                int total = 0;
                foreach (var file in EnumerateScripts())
                {
                    if (new FileInfo(file).Length > MaxFileBytes) continue;
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
                return HandlerOutcome.Ok(new JObject
                {
                    ["name"] = name,
                    ["count"] = total,
                    ["truncated"] = total > refs.Count,
                    ["resolution"] = "syntactic",
                    ["references"] = refs,
                    ["note"] = "Textual (syntactic) identifier matches in code; comments and string literals are excluded, but there is no semantic resolution (same-named members across types/overloads are not disambiguated)."
                });
            }
            catch (Exception e) { return Err($"Error finding references: {e.Message}"); }
        }

        public static HandlerOutcome GetSymbolBody(JObject p)
        {
            try
            {
                var path = p["path"]?.ToString();
                var name = p["name"]?.ToString();
                if (string.IsNullOrEmpty(path)) return Err("Missing required parameter: path", "VALIDATION_ERROR");
                if (string.IsNullOrEmpty(name)) return Err("Missing required parameter: name", "VALIDATION_ERROR");
                var full = ResolveScript(path);
                if (full == null) return Err($"Path is not a .cs file inside the project: {path}", "VALIDATION_ERROR");
                if (!File.Exists(full)) return Err($"File not found: {path}", "NOT_FOUND");

                var src = File.ReadAllText(full);
                var sym = Extract(src).FirstOrDefault(s => s.Name == name);
                if (sym == null) return Err($"Symbol '{name}' not found in {path}", "NOT_FOUND");

                var lines = src.Replace("\r\n", "\n").Split('\n');
                int start = Math.Max(0, sym.Line - 1);
                int end = Math.Min(sym.EndLine, lines.Length) - 1;
                var body = string.Join("\n", lines.Skip(start).Take(end - start + 1));
                return HandlerOutcome.Ok(new JObject
                {
                    ["path"] = Rel(full),
                    ["name"] = name,
                    ["kind"] = sym.Kind,
                    ["startLine"] = sym.Line,
                    ["endLine"] = sym.EndLine,
                    ["source"] = body
                });
            }
            catch (Exception e) { return Err($"Error getting symbol body: {e.Message}"); }
        }

        // ---- semantic-lite resolution (reflection + TypeCache over the compiled assemblies) ----
        // NAME-based: resolves identifiers/types to their compiled metadata. It does NOT bind by source
        // position (reflection has no source-location index) — same-named symbols are returned as a ranked
        // candidate list. Source-level binding is the Roslyn sidecar's job (a later milestone).

        public static HandlerOutcome ResolveSymbol(JObject p)
        {
            try
            {
                var name = p["name"]?.ToString();
                // path+position only EXTRACT the token name (the syntactic layer); they do not disambiguate.
                if (string.IsNullOrEmpty(name))
                {
                    var path = p["path"]?.ToString();
                    var pos = p["position"] as JObject;
                    if (!string.IsNullOrEmpty(path) && pos != null)
                    {
                        var full = ResolveScript(path);
                        if (full != null && File.Exists(full) && new FileInfo(full).Length <= MaxFileBytes)
                            name = IdentifierAt(File.ReadAllText(full), pos["line"]?.ToObject<int>() ?? 0, pos["column"]?.ToObject<int>() ?? 0);
                    }
                }
                if (string.IsNullOrEmpty(name))
                    return Err("Provide `name`, or `path`+`position` resolving to an identifier", "VALIDATION_ERROR");

                var max = Math.Max(1, Math.Min(p["maxResults"]?.ToObject<int?>() ?? 50, MaxResultsCeiling));
                const int scanCeiling = 50000; // bound the absent/rare-name scan (separate from the result cap)
                var candidates = new JArray();
                int scanned = 0;
                bool truncated = false;
                foreach (var t in EnumerateLoadedTypes())
                {
                    if (candidates.Count >= max) break;
                    if (++scanned > scanCeiling) { truncated = true; break; }
                    if (t.Name == name)
                        candidates.Add(new JObject {
                            ["type"] = t.FullName, ["kind"] = "type",
                            ["signature"] = t.FullName, ["visibility"] = t.IsPublic ? "public" : "internal",
                            ["assembly"] = t.Assembly.GetName().Name });
                    MemberInfo[] members;
                    try { members = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                    catch { continue; } // skip types whose members can't be reflected
                    foreach (var m in members)
                    {
                        if (candidates.Count >= max) break;
                        if (m.Name != name) continue;
                        candidates.Add(new JObject {
                            ["type"] = t.FullName, ["member"] = m.Name, ["kind"] = MemberKind(m),
                            ["signature"] = MemberSignature(m), ["visibility"] = MemberVisibility(m),
                            ["assembly"] = t.Assembly.GetName().Name });
                    }
                }
                return HandlerOutcome.Ok(new JObject { ["name"] = name, ["count"] = candidates.Count, ["truncated"] = truncated, ["candidates"] = candidates });
            }
            catch (Exception e) { return Err($"Error resolving symbol: {e.Message}"); }
        }

        public static HandlerOutcome GetTypeMembers(JObject p)
        {
            try
            {
                var typeName = p["typeName"]?.ToString();
                if (string.IsNullOrEmpty(typeName)) return Err("Missing required parameter: typeName", "VALIDATION_ERROR");
                var type = FindTypeByName(typeName, out int matchCount);
                if (type == null) return Err($"Type not found: {typeName}", "NOT_FOUND");
                var includeInherited = p["includeInherited"]?.ToObject<bool?>() ?? false;
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                if (!includeInherited) flags |= BindingFlags.DeclaredOnly;

                var members = new JArray();
                foreach (var m in type.GetMembers(flags))
                {
                    if (m is MethodInfo mi && mi.IsSpecialName) continue; // skip property get_/set_ accessors
                    var attrs = new JArray();
                    try { foreach (var a in m.GetCustomAttributes(false)) attrs.Add(a.GetType().Name); }
                    catch { /* skip attributes whose type can't be loaded */ }
                    members.Add(new JObject {
                        ["name"] = m.Name, ["kind"] = MemberKind(m), ["signature"] = MemberSignature(m),
                        ["visibility"] = MemberVisibility(m), ["attributes"] = attrs });
                }
                var result = new JObject { ["type"] = type.FullName, ["count"] = members.Count, ["members"] = members };
                if (matchCount > 1) { result["ambiguous"] = true; result["ambiguousMatches"] = matchCount; }
                return HandlerOutcome.Ok(result);
            }
            catch (Exception e) { return Err($"Error reading type members: {e.Message}"); }
        }

        public static HandlerOutcome FindImplementations(JObject p)
        {
            try
            {
                var typeName = p["typeName"]?.ToString();
                if (string.IsNullOrEmpty(typeName)) return Err("Missing required parameter: typeName", "VALIDATION_ERROR");
                var type = FindTypeByName(typeName, out int matchCount);
                if (type == null) return Err($"Type not found: {typeName}", "NOT_FOUND");

                var implementors = new JArray();
                // TypeCache.GetTypesDerivedFrom covers both subclasses and interface implementors.
                foreach (var t in TypeCache.GetTypesDerivedFrom(type))
                {
                    if (t.FullName == null) continue;
                    implementors.Add(new JObject {
                        ["type"] = t.FullName, ["assembly"] = t.Assembly.GetName().Name,
                        ["kind"] = t.IsInterface ? "interface" : (t.IsAbstract ? "abstract" : "class") });
                }
                var result = new JObject { ["type"] = type.FullName, ["count"] = implementors.Count, ["implementors"] = implementors };
                if (matchCount > 1) { result["ambiguous"] = true; result["ambiguousMatches"] = matchCount; }
                return HandlerOutcome.Ok(result);
            }
            catch (Exception e) { return Err($"Error finding implementations: {e.Message}"); }
        }

        // BCL/framework assemblies are huge and rarely the search target — order them LAST so name lookups
        // hit user/Unity/package types first (the scan ceiling then caps the framework tail, not user code).
        private static bool IsFrameworkAssembly(string name) =>
            name == "mscorlib" || name == "netstandard" ||
            name.StartsWith("System", StringComparison.Ordinal) ||
            name.StartsWith("Mono.", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            name.StartsWith("nunit", StringComparison.Ordinal);

        private static IEnumerable<Type> EnumerateLoadedTypes()
        {
            var ordered = AppDomain.CurrentDomain.GetAssemblies()
                .OrderBy(a => IsFrameworkAssembly(a.GetName().Name) ? 1 : 0);
            foreach (var asm in ordered)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; } // tolerate partially-loadable assemblies
                catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || t.FullName == null) continue; // skip generic-parameter / unnamed types
                    yield return t;
                }
            }
        }

        // Resolve a simple or full type name; an exact FullName match wins, else the first simple-name match.
        // simpleMatchCount > 1 signals an undisclosed collision the caller should disambiguate with a full name.
        private static Type FindTypeByName(string typeName, out int simpleMatchCount)
        {
            Type exact = null, firstSimple = null;
            int simple = 0;
            foreach (var t in EnumerateLoadedTypes())
            {
                if (exact == null && t.FullName == typeName) exact = t;
                if (t.Name == typeName) { simple++; if (firstSimple == null) firstSimple = t; }
            }
            simpleMatchCount = exact != null ? 1 : simple; // an exact full-name match is unambiguous
            return exact ?? firstSimple;
        }

        private static string MemberKind(MemberInfo m)
        {
            switch (m)
            {
                case MethodInfo _: return "method";
                case PropertyInfo _: return "property";
                case FieldInfo _: return "field";
                case EventInfo _: return "event";
                case ConstructorInfo _: return "constructor";
                case Type _: return "type";
                default: return "member";
            }
        }

        private static string MemberSignature(MemberInfo m)
        {
            if (m is MethodInfo mi)
                return $"{mi.ReturnType.Name} {mi.Name}({string.Join(", ", Array.ConvertAll(mi.GetParameters(), x => x.ParameterType.Name + " " + x.Name))})";
            if (m is PropertyInfo pi) return $"{pi.PropertyType.Name} {pi.Name}";
            if (m is FieldInfo fi) return $"{fi.FieldType.Name} {fi.Name}";
            return m.Name;
        }

        private static string MemberVisibility(MemberInfo m)
        {
            switch (m)
            {
                case MethodBase mb: return AccessLevel(mb.IsPublic, mb.IsPrivate, mb.IsFamily, mb.IsFamilyOrAssembly, mb.IsFamilyAndAssembly);
                case FieldInfo fi: return AccessLevel(fi.IsPublic, fi.IsPrivate, fi.IsFamily, fi.IsFamilyOrAssembly, fi.IsFamilyAndAssembly);
                case PropertyInfo pi:
                    var acc = pi.GetMethod ?? pi.SetMethod;
                    return acc != null ? AccessLevel(acc.IsPublic, acc.IsPrivate, acc.IsFamily, acc.IsFamilyOrAssembly, acc.IsFamilyAndAssembly) : "n/a";
                default: return "n/a";
            }
        }

        // Map the reflection access flags to the C# keyword. Exactly one flag is set per member.
        private static string AccessLevel(bool pub, bool priv, bool fam, bool famOrAsm, bool famAndAsm)
        {
            if (pub) return "public";
            if (priv) return "private";
            if (famAndAsm) return "private protected";
            if (famOrAsm) return "protected internal";
            if (fam) return "protected";
            return "internal";
        }

        // Extract the identifier token covering (line, column); both 1-based.
        private static string IdentifierAt(string src, int line, int column)
        {
            if (line < 1 || column < 1) return null;
            var lines = src.Replace("\r\n", "\n").Split('\n');
            if (line > lines.Length) return null;
            var text = lines[line - 1];
            int i = column - 1;
            if (i < 0 || i >= text.Length) return null;
            bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
            if (!IsIdent(text[i])) return null;
            int start = i, end = i;
            while (start > 0 && IsIdent(text[start - 1])) start--;
            while (end + 1 < text.Length && IsIdent(text[end + 1])) end++;
            return text.Substring(start, end - start + 1);
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
            "typeof","nameof","default","do","else","get","set","add","remove","yield","await","throw","when",
            "base","this"
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
                // skip member-access calls ("obj.Method(") and object construction ("new Foo(args) { }")
                int b = m.Index - 1;
                while (b >= 0 && char.IsWhiteSpace(masked[b])) b--;
                if (b >= 0 && masked[b] == '.') continue;
                if (b >= 0 && (char.IsLetterOrDigit(masked[b]) || masked[b] == '_'))
                {
                    int ws = b;
                    while (ws >= 0 && (char.IsLetterOrDigit(masked[ws]) || masked[ws] == '_')) ws--;
                    if (masked.Substring(ws + 1, b - ws) == "new") continue;
                }
                int line = LineNumberAt(lineStarts, m.Index);
                var term = m.Groups[2].Value;
                int end;
                if (term == "{")
                {
                    end = MatchBlockEnd(masked, lineStarts, m.Index, line);
                }
                else if (term == "where")
                {
                    // Generic constraint: any body starts after the constraint clause. A ';' before the next
                    // '{' means a bodyless declaration (abstract/interface) — keep it on its line so the
                    // brace scan doesn't run into the NEXT method's body.
                    int after = m.Index + m.Length;
                    int nextBrace = masked.IndexOf('{', after);
                    int nextSemi = masked.IndexOf(';', after);
                    end = (nextBrace >= 0 && (nextSemi < 0 || nextBrace < nextSemi))
                        ? MatchBlockEnd(masked, lineStarts, after, line)
                        : line;
                }
                else
                {
                    end = line; // expression-bodied (=>) is self-contained on its line
                }
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
            var root = Directory.GetParent(Application.dataPath)?.FullName;
            if (root == null) return null;
            var projectRoot = Path.GetFullPath(root).Replace("\\", "/").TrimEnd('/');
            var full = (Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path))).Replace("\\", "/");
            // Containment guard: only files inside the project root may be read — blocks absolute paths
            // outside the project and ".." traversal. Returning null routes callers to an error envelope.
            if (!(full.Equals(projectRoot, StringComparison.OrdinalIgnoreCase) ||
                  full.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase)))
                return null;
            return full;
        }

        private static string Rel(string full)
        {
            var root = Directory.GetParent(Application.dataPath)?.FullName?.Replace("\\", "/");
            full = full.Replace("\\", "/");
            return (root != null && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                ? full.Substring(root.Length).TrimStart('/')
                : full;
        }

        private static HandlerOutcome Err(string error, string code = "INTERNAL_ERROR") => HandlerOutcome.Fail(error, code);
    }
}
