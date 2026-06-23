using System;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // Static target the invoke tests resolve via reflection across loaded assemblies.
    public static class InvokeTestTarget
    {
        public static int Add(int a, int b) => a + b;
        public static void DoVoid() { }
        public static int Boom() => throw new InvalidOperationException("boom");
    }

    // invoke_static_method (G6) + InvokePolicy default-deny gate (H2). Drives the allow-list via the env var.
    public class StaticInvokeTests
    {
        private const string TypeName = "UnityEditorMCP.Tests.InvokeTestTarget";

        [SetUp] public void SetUp() { Environment.SetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW", null); }
        [TearDown] public void TearDown() { Environment.SetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW", null); }

        [Test]
        public void DeniedByDefault()
        {
            var r = StaticInvokeHandler.InvokeStaticMethod(new JObject { ["typeName"] = TypeName, ["methodName"] = "Add", ["args"] = new JArray(1, 2) });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("INVOKE_DENIED", r.Code);
        }

        [Test]
        public void AllowedByEnv_ReturnsResult()
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW", TypeName + ".*");
            var r = StaticInvokeHandler.InvokeStaticMethod(new JObject { ["typeName"] = TypeName, ["methodName"] = "Add", ["args"] = new JArray(2, 3) });
            Assert.IsFalse(r.IsError, r.Error);
            Assert.AreEqual(5, (int)JObject.FromObject(r.Payload)["result"]);
        }

        [Test]
        public void VoidMethod_IsVoidTrue()
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW", "*");
            var r = StaticInvokeHandler.InvokeStaticMethod(new JObject { ["typeName"] = TypeName, ["methodName"] = "DoVoid" });
            Assert.IsFalse(r.IsError, r.Error);
            Assert.IsTrue((bool)JObject.FromObject(r.Payload)["isVoid"]);
        }

        [Test]
        public void ThrowingMethod_InvocationError()
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW", "*");
            var r = StaticInvokeHandler.InvokeStaticMethod(new JObject { ["typeName"] = TypeName, ["methodName"] = "Boom" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("INVOCATION_ERROR", r.Code);
        }

        [Test]
        public void UnknownType_NotFound()
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW", "*");
            var r = StaticInvokeHandler.InvokeStaticMethod(new JObject { ["typeName"] = "No.Such.Type", ["methodName"] = "X" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("NOT_FOUND", r.Code);
        }

        [Test]
        public void MissingParams_ValidationError()
        {
            var r = StaticInvokeHandler.InvokeStaticMethod(new JObject { ["methodName"] = "Add" });
            Assert.IsTrue(r.IsError);
            Assert.AreEqual("VALIDATION_ERROR", r.Code);
        }

        [Test]
        public void ExactPattern_ScopesToThatMethod()
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_INVOKE_ALLOW", TypeName + ".Add");
            var ok = StaticInvokeHandler.InvokeStaticMethod(new JObject { ["typeName"] = TypeName, ["methodName"] = "Add", ["args"] = new JArray(1, 1) });
            Assert.IsFalse(ok.IsError, ok.Error);
            var denied = StaticInvokeHandler.InvokeStaticMethod(new JObject { ["typeName"] = TypeName, ["methodName"] = "DoVoid" });
            Assert.IsTrue(denied.IsError);
            Assert.AreEqual("INVOKE_DENIED", denied.Code);
        }
    }
}
