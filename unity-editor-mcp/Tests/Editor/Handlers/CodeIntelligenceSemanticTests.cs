using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // Fixture types the lite resolver must find in the compiled test assembly.
    public interface ICodeIntelFixture { void Ping(); }
    public class CodeIntelFixtureBase : ICodeIntelFixture { public int Health; public void Ping() { } }
    public class CodeIntelFixtureDerived : CodeIntelFixtureBase { public void Attack(int power) { } }
    public class CodeIntelFixtureOther { public void Attack() { } protected void Guard() { } } // shares "Attack"; has a protected member

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
            var attackCount = 0;
            foreach (var c in (JArray)data["candidates"])
                if ((string)c["member"] == "Attack")
                {
                    attackCount++;
                    if ((string)c["type"] == "UnityEditorMCP.Tests.CodeIntelFixtureDerived") found = true;
                }
            Assert.IsTrue(found, "Attack must resolve to its declaring type");
            Assert.GreaterOrEqual(attackCount, 2, "Attack is on two fixture types — name-based resolution returns both (ambiguous)");
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
            var foundBase = false; var foundDerived = false;
            foreach (var x in (JArray)data["implementors"])
            {
                if ((string)x["type"] == "UnityEditorMCP.Tests.CodeIntelFixtureBase") foundBase = true;
                if ((string)x["type"] == "UnityEditorMCP.Tests.CodeIntelFixtureDerived") foundDerived = true;
            }
            Assert.IsTrue(foundBase, "CodeIntelFixtureBase implements ICodeIntelFixture");
            Assert.IsTrue(foundDerived, "CodeIntelFixtureDerived (subclass) also implements ICodeIntelFixture");
        }

        [Test]
        public void FindReferences_ResultIsTaggedSyntactic()
        {
            var outcome = CodeIntelligenceHandler.FindReferences(new JObject { ["name"] = "CodeIntelFixtureDerived" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            Assert.AreEqual("syntactic", (string)data["resolution"]);
            Assert.IsNotNull(data["references"], "references array must be present");
        }

        [Test]
        public void ResolveSymbol_MaxResults_CapsCandidates()
        {
            var outcome = CodeIntelligenceHandler.ResolveSymbol(new JObject { ["name"] = "Update", ["maxResults"] = 1 });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            Assert.LessOrEqual((int)data["count"], 1, "maxResults must cap the candidate count");
        }

        [Test]
        public void GetTypeMembers_ReportsProtectedVisibility()
        {
            var outcome = CodeIntelligenceHandler.GetTypeMembers(new JObject { ["typeName"] = "CodeIntelFixtureOther" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            var guardIsProtected = false;
            foreach (var m in (JArray)data["members"])
                if ((string)m["name"] == "Guard" && (string)m["visibility"] == "protected") guardIsProtected = true;
            Assert.IsTrue(guardIsProtected, "a protected method must report visibility \"protected\", not \"internal\"");
        }

        [Test]
        public void GetTypeMembers_SimpleNameCollision_FlagsAmbiguous()
        {
            // "Object" exists as System.Object, UnityEngine.Object, ... — a simple-name collision.
            var outcome = CodeIntelligenceHandler.GetTypeMembers(new JObject { ["typeName"] = "Object" });
            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            Assert.IsTrue(data["ambiguous"] != null && (bool)data["ambiguous"], "\"Object\" collides across namespaces -> ambiguous");
            Assert.GreaterOrEqual((int)data["ambiguousMatches"], 2);
        }
    }
}
