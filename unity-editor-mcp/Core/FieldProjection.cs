using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// GraphQL-style field projection over a result payload. Callers pass dot-paths
    /// (e.g. "objects.name"); the payload is trimmed to the union of those paths so an agent only
    /// pays for the fields it asked for. Free-form and LENIENT: an unknown path is silently omitted
    /// (the caller may guess field names — there is no schema). Arrays are TRANSPARENT: a path
    /// describes object structure and is applied to every element of an array it meets.
    ///
    /// Pure and Unity-independent — covered by `dotnet test`. Applied once by the dispatcher to every
    /// command's SUCCESS payload, so no handler does its own projection.
    /// </summary>
    public static class FieldProjection
    {
        /// <summary>
        /// Projects <paramref name="payload"/> to the union of <paramref name="paths"/> (dot-paths).
        /// A null payload, or null/empty paths, returns the payload unchanged.
        /// </summary>
        public static JToken Project(JToken payload, IReadOnlyList<string> paths)
        {
            if (payload == null || paths == null || paths.Count == 0) return payload;

            var split = paths
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p.Split('.').Where(s => s.Length > 0).ToArray())
                .Where(segs => segs.Length > 0)
                .ToList();
            if (split.Count == 0) return payload;

            var projected = ProjectSegments(payload, split);
            // Guard against SILENT TOTAL DATA LOSS: if a projection matches NOTHING at the top level (every requested
            // path a typo / case-mismatch), return the FULL payload rather than an empty {} that reads as empty
            // success — the caller sees their data and can fix `fields`. (Bug hunt: all-miss projection.)
            if (payload is JObject src && src.Count > 0 && projected is JObject proj && proj.Count == 0)
                return payload;
            return projected;
        }

        private static JToken ProjectSegments(JToken token, List<string[]> paths)
        {
            // Arrays are transparent: project every element by the same paths.
            if (token is JArray arr)
            {
                var outArr = new JArray();
                foreach (var el in arr) outArr.Add(ProjectSegments(el, paths));
                return outArr;
            }

            if (token is JObject obj)
            {
                var result = new JObject();
                foreach (var group in paths.GroupBy(p => p[0]))
                {
                    var head = group.Key;
                    // Case-INSENSITIVE head match — a "Name" vs "name" mismatch previously dropped the whole key. Use
                    // the payload's ACTUAL key for output so the projection preserves real casing instead of RENAMING
                    // the field to the caller's requested casing. (Bug hunt: case match + rename.)
                    string actualKey = null; JToken child = null;
                    foreach (var prop in obj.Properties())
                        if (string.Equals(prop.Name, head, System.StringComparison.OrdinalIgnoreCase)) { actualKey = prop.Name; child = prop.Value; break; }
                    if (actualKey == null) continue; // missing → omit (lenient)

                    // A leaf selection ("head") includes the whole subtree; deeper paths refine.
                    // If both are present for the same head, the leaf wins (whole subtree).
                    var leaf = group.Any(p => p.Length == 1);
                    var tails = group.Where(p => p.Length > 1).Select(p => p.Skip(1).ToArray()).ToList();
                    result[actualKey] = (leaf || tails.Count == 0)
                        ? child
                        : ProjectSegments(child, tails);
                }
                return result;
            }

            // Scalar (or null): nothing to descend into.
            return token;
        }
    }
}
