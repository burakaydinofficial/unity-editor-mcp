using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Core
{
    /// <summary>Append-only command journal (H5). Unity-independent (the file path is injected) so it runs under
    /// dotnet test. Every method is FAIL-SAFE: audit logging must never break command dispatch or reads.</summary>
    public static class AuditLog
    {
        /// <summary>Append one entry {t,type,target,ok}. If the file exceeds capBytes, drop the oldest half
        /// first (crude rotation — the truncate is occasional, so most appends are O(1)).</summary>
        public static void Append(string filePath, string type, string target, bool ok, long capBytes = 2_097_152)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(type)) return;
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (capBytes > 0 && File.Exists(filePath) && new FileInfo(filePath).Length > capBytes)
                {
                    var lines = File.ReadAllLines(filePath);
                    File.WriteAllLines(filePath, lines.Skip(Math.Max(1, lines.Length / 2))); // always drop >=1 (guarantees shrinkage)
                }
                var entry = new JObject
                {
                    ["t"] = DateTime.UtcNow.ToString("o"),
                    ["type"] = type,
                    ["target"] = target ?? "",
                    ["ok"] = ok
                };
                File.AppendAllText(filePath, entry.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            }
            catch { /* logging must never throw */ }
        }

        /// <summary>The last <paramref name="max"/> entries (chronological), filtered by a case-insensitive
        /// type substring and a since-timestamp (ISO-8601). Malformed lines are skipped.</summary>
        public static JArray Read(string filePath, int max, string typeFilter, string since)
        {
            var result = new JArray();
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return result;
                if (max <= 0) max = 100;
                if (max > 1000) max = 1000;
                var sinceNorm = NormalizeSince(since); // canonicalize so a short-form since (no fractional / +00:00) still matches
                var matches = new List<JObject>();
                foreach (var line in File.ReadLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject e;
                    try
                    {
                        // DateParseHandling.None: keep "t" as the raw ISO string so the ordinal since-compare works
                        // (Newtonsoft otherwise reparses it into a DateTime token, changing its string form).
                        using (var sr = new StringReader(line))
                        using (var jr = new Newtonsoft.Json.JsonTextReader(sr) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                            e = (JObject)JToken.ReadFrom(jr);
                    }
                    catch { continue; }
                    if (!string.IsNullOrEmpty(typeFilter) &&
                        (e["type"]?.ToString().IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) ?? -1) < 0) continue;
                    if (!string.IsNullOrEmpty(sinceNorm) &&
                        string.CompareOrdinal(e["t"]?.ToString() ?? "", sinceNorm) < 0) continue;
                    matches.Add(e);
                }
                foreach (var e in matches.Skip(Math.Max(0, matches.Count - max))) result.Add(e);
            }
            catch { /* fail-safe */ }
            return result;
        }

        // Re-emit a caller's `since` in the stored canonical form ("o", UTC) so the ordinal compare is correct for
        // any ISO-8601 input (no fractional seconds, +00:00 offset, etc.). Unparseable -> raw (best-effort).
        private static string NormalizeSince(string since)
        {
            if (string.IsNullOrEmpty(since)) return since;
            if (DateTime.TryParse(since, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToUniversalTime().ToString("o");
            return since;
        }

        public static void Clear(string filePath)
        {
            try { if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) File.Delete(filePath); }
            catch { /* fail-safe */ }
        }
    }
}
