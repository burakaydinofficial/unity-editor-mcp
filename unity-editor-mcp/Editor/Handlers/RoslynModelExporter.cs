using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>Exports Unity's real CompilationPipeline model to Library/UnityEditorMCP/roslyn-model.json
    /// for the out-of-process Roslyn sidecar (ADR 0006). Floor-safe — CompilationPipeline is 2017.3+,
    /// and this uses only it + System.IO + Newtonsoft (no version-divergent API).</summary>
    public static class RoslynModelExporter
    {
        public static HandlerOutcome Export(JObject p)
        {
            try
            {
                var projectRoot = Path.GetFullPath(Application.dataPath + "/..");
                var dir = Path.Combine(projectRoot, "Library", "UnityEditorMCP");
                Directory.CreateDirectory(dir);
                var modelPath = Path.Combine(dir, "roslyn-model.json");

                var assemblies = new JArray();
                foreach (var asm in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
                {
                    var refs = new JArray();
                    foreach (var r in asm.allReferences) refs.Add(r);
                    var defines = new JArray();
                    foreach (var d in asm.defines) defines.Add(d);
                    var sources = new JArray();
                    foreach (var s in asm.sourceFiles) sources.Add(Path.GetFullPath(s));
                    assemblies.Add(new JObject {
                        ["name"] = asm.name, ["sourceFiles"] = sources, ["references"] = refs,
                        ["defines"] = defines, ["langVersion"] = "8.0" });
                }
                var generation = System.DateTime.UtcNow.Ticks;
                var model = new JObject { ["generation"] = generation, ["assemblies"] = assemblies };
                File.WriteAllText(modelPath, model.ToString());
                return HandlerOutcome.Ok(new JObject { ["modelPath"] = modelPath, ["generation"] = generation, ["assemblies"] = assemblies.Count });
            }
            catch (System.Exception e) { return HandlerOutcome.Fail($"Failed to export Roslyn model: {e.Message}"); }
        }
    }
}
