using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    public class RoslynModelExporterTests
    {
        [Test]
        public void Export_WritesModelFileWithAssembliesAndGeneration()
        {
            var outcome = RoslynModelExporter.Export(new JObject());
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            Assert.IsTrue(System.IO.File.Exists((string)data["modelPath"]));
            var model = JObject.Parse(System.IO.File.ReadAllText((string)data["modelPath"]));
            Assert.GreaterOrEqual(((JArray)model["assemblies"]).Count, 1);
            Assert.IsNotNull(model["generation"]);
        }
    }
}
