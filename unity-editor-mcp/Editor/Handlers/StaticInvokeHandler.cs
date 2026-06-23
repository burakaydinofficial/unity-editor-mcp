using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// G6: invoke a static method by type + name with JSON args. Arbitrary code execution, so every call is
    /// gated by <see cref="InvokePolicy"/> (H2 default-deny) — denied unless explicitly allow-listed.
    /// </summary>
    public static class StaticInvokeHandler
    {
        public static HandlerOutcome InvokeStaticMethod(JObject parameters)
        {
            try
            {
                string typeName = parameters["typeName"]?.ToString();
                string methodName = parameters["methodName"]?.ToString();
                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
                    return HandlerOutcome.Fail("typeName and methodName are required", "VALIDATION_ERROR");

                if (!InvokePolicy.IsAllowed(typeName, methodName))
                    return HandlerOutcome.Fail(
                        $"Static invoke denied by policy: {typeName}.{methodName}. It is default-deny — allow it via the UNITY_MCP_INVOKE_ALLOW env var (comma-separated) or ProjectSettings/UnityEditorMcpInvokePolicy.json (\"allowInvoke\": [...]).",
                        "INVOKE_DENIED");

                var type = ResolveType(typeName, parameters["assemblyName"]?.ToString());
                if (type == null)
                    return HandlerOutcome.Fail($"Type not found: {typeName}", "NOT_FOUND");

                var argsArr = parameters["args"] as JArray ?? new JArray();
                var candidates = type
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name == methodName && m.GetParameters().Length == argsArr.Count)
                    .ToList();
                if (candidates.Count == 0)
                    return HandlerOutcome.Fail($"No static method '{methodName}' with {argsArr.Count} parameter(s) on {typeName}", "NOT_FOUND");
                if (candidates.Count > 1)
                    return HandlerOutcome.Fail($"Ambiguous: {candidates.Count} static '{methodName}' overloads with {argsArr.Count} parameter(s) on {typeName}", "AMBIGUOUS");

                var method = candidates[0];
                var ps = method.GetParameters();
                var callArgs = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    try { callArgs[i] = argsArr[i].ToObject(ps[i].ParameterType); }
                    catch (Exception ex)
                    {
                        return HandlerOutcome.Fail($"Argument {i} ('{ps[i].Name}') cannot convert to {ps[i].ParameterType.Name}: {ex.Message}", "VALIDATION_ERROR");
                    }
                }

                object result;
                try { result = method.Invoke(null, callArgs); }
                catch (TargetInvocationException tie)
                {
                    return HandlerOutcome.Fail($"Invoked method threw: {tie.InnerException?.Message ?? tie.Message}", "INVOCATION_ERROR");
                }

                bool isVoid = method.ReturnType == typeof(void);
                object resultPayload = null;
                if (!isVoid && result != null)
                {
                    try { resultPayload = JToken.FromObject(result); }
                    catch { resultPayload = result.ToString(); }
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    type = type.FullName,
                    method = methodName,
                    returnType = method.ReturnType.Name,
                    isVoid = isVoid,
                    result = resultPayload
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[StaticInvokeHandler] invoke_static_method failed: {e.Message}");
                return HandlerOutcome.Fail($"invoke_static_method failed: {e.Message}");
            }
        }

        private static Type ResolveType(string typeName, string assemblyName)
        {
            var t = Type.GetType(typeName);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.IsNullOrEmpty(assemblyName) &&
                    !asm.GetName().Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { t = asm.GetType(typeName); if (t != null) return t; }
                catch { /* some dynamic assemblies throw on GetType — skip */ }
            }
            return null;
        }
    }
}
