using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Feedback #3: execute_menu_item now returns the console output emitted DURING the invoke (menu methods are
    /// void — no return value — but their logs are capturable), so the agent doesn't need a separate log read.
    /// </summary>
    public class MenuHandlerLogCaptureTests
    {
        public const string ProbeMsg = "MCP_MENU_LOG_PROBE_42";

        // A controlled menu item that logs a known error, so the test can assert it was captured.
        [MenuItem("MCPTest/LogProbe")]
        public static void LogProbe() { Debug.LogError(ProbeMsg); }

        [Test]
        public void ExecuteMenuItem_CapturesLogsEmittedDuringInvoke()
        {
            LogAssert.Expect(LogType.Error, ProbeMsg); // the probe logs this on purpose — don't fail the test on it

            var outcome = MenuHandler.ExecuteMenuItem(new JObject { ["menuPath"] = "MCPTest/LogProbe" });

            Assert.IsFalse(outcome.IsError, outcome.Error);
            var data = JObject.FromObject(outcome.Payload);
            var logs = (JArray)data["logs"];
            Assert.IsNotNull(logs, "result must include a logs array");
            Assert.IsTrue(logs.Any(l => (string)l["message"] == ProbeMsg), "the menu's Debug.LogError must be captured");
            Assert.IsTrue((int)data["errorCount"] >= 1, "errorCount must count the captured error");
        }
    }
}
