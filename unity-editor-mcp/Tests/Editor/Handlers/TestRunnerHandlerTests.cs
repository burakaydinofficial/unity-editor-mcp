using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Feedback: the test-runner's persistent "running" latch could stick true and wedge run_tests (the model thought
    /// a run was active when none was, and couldn't stop it). These pin the deterministic, no-nested-run behaviours:
    /// a no-match filter must NOT latch (the likely wedge trigger), and cancel_tests must clear a stale/stuck flag.
    /// The live run/observe path is covered by the E2E harness (a run can't be started inside a running test).
    /// The SessionState keys below mirror the private consts in TestRunnerHandler.
    /// </summary>
    public class TestRunnerHandlerTests
    {
        private const string RunningKey = "UnityEditorMCP.TestRunner.IsRunning";
        private const string RunModeKey = "UnityEditorMCP.TestRunner.RunMode";
        private const string LastActivityKey = "UnityEditorMCP.TestRunner.LastActivityUtc";

        [SetUp] public void Setup()
        {
            // Clean slate so tests are isolated and never inherit a latched flag.
            SessionState.SetBool(RunningKey, false);
            SessionState.EraseString(LastActivityKey);
        }

        [Test] public void RunTests_NoMatchingFilter_DoesNotLatch()
        {
            var probe = new JObject { ["testMode"] = "EditMode", ["testNames"] = new JArray { "No.Such.Test.Exists_ZZZ" } };

            var first = TestRunnerHandler.RunTests(probe);
            Assert.IsFalse(first.IsError, first.Error);
            StringAssert.Contains("nothing to run", JObject.FromObject(first.Payload)["message"].ToString().ToLower());

            // Not latched: a second call also reports nothing-to-run, NOT "already running" (the wedge is prevented).
            var second = TestRunnerHandler.RunTests(probe);
            Assert.IsFalse(second.IsError, second.Error);
            StringAssert.Contains("nothing to run", JObject.FromObject(second.Payload)["message"].ToString().ToLower());
        }

        [Test] public void CancelTests_NothingRunning_ReportsNoRun()
        {
            var r = TestRunnerHandler.CancelTests(new JObject());
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);
            Assert.IsFalse((bool)data["wasCancelled"]);
            StringAssert.Contains("no tests", data["message"].ToString().ToLower());
        }

        [Test] public void CancelTests_ClearsStaleStuckState_WithoutForce()
        {
            // Reproduce the reported failure: a stuck EditMode 'running' latch with no recent callback activity, no real run.
            SessionState.SetBool(RunningKey, true);
            SessionState.SetString(RunModeKey, "EditMode");
            SessionState.SetString(LastActivityKey, System.DateTime.UtcNow.AddMinutes(-10).ToString("o"));

            var r = TestRunnerHandler.CancelTests(new JObject()); // no force — a STALE latch must self-clear, not refuse
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);
            Assert.IsTrue((bool)data["stateReset"], "a stale stuck flag must be cleared, not refused");
            Assert.IsFalse(SessionState.GetBool(RunningKey, false), "the running flag must be reset");
        }
    }
}
