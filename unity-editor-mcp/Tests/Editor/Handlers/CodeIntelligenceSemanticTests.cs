using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // Fixture types the lite resolver must find in the compiled test assembly.
    public interface ICodeIntelFixture { void Ping(); }
    public class CodeIntelFixtureBase : ICodeIntelFixture { public int Health; public void Ping() { } }
    public class CodeIntelFixtureDerived : CodeIntelFixtureBase { public void Attack(int power) { } }

    /// <summary>
    /// Lite (name-based) semantic resolution over Unity's compiled assemblies (reflection + TypeCache).
    /// These are NOT source-position bindings — resolve_symbol returns a ranked candidate list.
    /// </summary>
    public class CodeIntelligenceSemanticTests
    {
        [Test]
        public void ResolveSymbol_ByName_ReturnsTypeAndMemberCandidates()
        {
            var outcome = CodeIntelligenceHandler.ResolveSymbol(new JObject { ["name"] = "CodeIntelFixtureDerived" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            Assert.GreaterOrEqual((int)data["count"], 1);
            var hasType = false;
            foreach (var c in (JArray)data["candidates"])
                if ((string)c["type"] == "UnityEditorMCP.Tests.CodeIntelFixtureDerived" && (string)c["kind"] == "type") hasType = true;
            Assert.IsTrue(hasType, "the fixture type must be a candidate");
        }

        [Test]
        public void ResolveSymbol_MemberName_ReturnsMemberCandidatesWithDeclaringType()
        {
            var outcome = CodeIntelligenceHandler.ResolveSymbol(new JObject { ["name"] = "Attack" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            var found = false;
            foreach (var c in (JArray)data["candidates"])
                if ((string)c["member"] == "Attack" && (string)c["type"] == "UnityEditorMCP.Tests.CodeIntelFixtureDerived") found = true;
            Assert.IsTrue(found, "Attack must resolve to its declaring type");
        }

        [Test]
        public void ResolveSymbol_MissingNameAndPath_IsValidationError()
        {
            var outcome = CodeIntelligenceHandler.ResolveSymbol(new JObject());
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
        }

        [Test]
        public void GetTypeMembers_ReturnsDeclaredMembersWithSignatures()
        {
            var outcome = CodeIntelligenceHandler.GetTypeMembers(new JObject { ["typeName"] = "CodeIntelFixtureDerived" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            var hasAttack = false;
            foreach (var m in (JArray)data["members"])
                if ((string)m["name"] == "Attack" && (string)m["kind"] == "method") hasAttack = true;
            Assert.IsTrue(hasAttack);
        }

        [Test]
        public void GetTypeMembers_UnknownType_IsNotFound()
        {
            var outcome = CodeIntelligenceHandler.GetTypeMembers(new JObject { ["typeName"] = "NoSuchType_xyz" });
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual("NOT_FOUND", outcome.Code);
        }

        [Test]
        public void FindImplementations_Interface_ReturnsImplementors()
        {
            var outcome = CodeIntelligenceHandler.FindImplementations(new JObject { ["typeName"] = "ICodeIntelFixture" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            var found = false;
            foreach (var x in (JArray)data["implementors"])
                if ((string)x["type"] == "UnityEditorMCP.Tests.CodeIntelFixtureBase") found = true;
            Assert.IsTrue(found, "CodeIntelFixtureBase implements ICodeIntelFixture");
        }

        [Test]
        public void FindReferences_ResultIsTaggedSyntactic()
        {
            var outcome = CodeIntelligenceHandler.FindReferences(new JObject { ["name"] = "CodeIntelFixtureDerived" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            Assert.AreEqual("syntactic", (string)data["resolution"]);
        }
    }
}
