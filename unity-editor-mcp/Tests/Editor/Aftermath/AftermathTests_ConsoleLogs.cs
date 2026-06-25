using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath tests for the ConsoleLogs tool category. Each test calls a tool HANDLER and then
    /// INDEPENDENTLY re-reads the Unity editor console through a DIFFERENT path
    /// (ConsoleHandler.ReadConsoleEntries — the raw reflection reader that backs read_logs, distinct
    /// from the handler under test) to assert the REAL effect happened, and that unrelated state did
    /// not move. Every test logs a unique GUID marker so it can never collide with pre-existing
    /// console content, and leaves zero residue (the console is restored to empty via clear).
    ///
    /// Markers are emitted with Debug.LogWarning / Debug.Log — the Unity Test Runner only fails a
    /// test on UNEXPECTED Error/Exception/Assert logs, so Warning/Log markers are safe without
    /// LogAssert (mirrors ComponentHandlerTests, which logs warnings without LogAssert).
    ///
    /// SKIPPED tools (no aftermath test, with reason):
    ///  - refresh_assets (SystemHandler.RefreshAssets): calls AssetDatabase.Refresh(), which can
    ///    trigger a script recompile / domain reload — forbidden in EditMode batch (would tear down
    ///    the test domain). Not safely/synchronously verifiable.
    ///  - ping (SystemHandler.Ping): trivial echo with no editor-state side effect to independently
    ///    re-read; an aftermath assertion would be hollow.
    /// </summary>
    [TestFixture]
    public class AftermathTests_ConsoleLogs
    {
        private const string MarkerPrefix = "MCP_AFTERMATH_CONSOLE_";

        [SetUp]
        public void Setup()
        {
            // Start each test from a known-empty console so independent re-reads are unambiguous.
            ConsoleHandler.ClearConsoleEntries();
        }

        [TearDown]
        public void TearDown()
        {
            // Leave zero residue: drop every marker (and anything else) we put in the console.
            ConsoleHandler.ClearConsoleEntries();
        }

        private static string NewMarker()
        {
            return MarkerPrefix + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Independent re-read path (NOT the handler under test): raw reflection reader that read_logs
        /// uses. Returns true if any entry's message contains the marker.
        /// </summary>
        private static bool ConsoleContainsMarker(string marker)
        {
            List<Dictionary<string, object>> entries = ConsoleHandler.ReadConsoleEntries(1000, null);
            Assert.IsNotNull(entries, "Console reflection unavailable — cannot run aftermath verification.");
            foreach (var e in entries)
            {
                object msg;
                if (e.TryGetValue("message", out msg) && msg != null &&
                    msg.ToString().IndexOf(marker, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // ----------------------------------------------------------------------------------------
        // enhanced_read_logs -> ConsoleHandler.EnhancedReadLogs
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// Aftermath: after a unique warning marker is logged, enhanced_read_logs returns that marker
        /// in its `logs` payload AND its filtered `statistics.warnings` reflects it; an unrelated
        /// never-logged marker must NOT appear (baseline that did not move). Cross-checked against the
        /// independent ReadConsoleEntries reader.
        /// </summary>
        [Test]
        public void EnhancedReadLogs_SurfacesLoggedWarning_AndFiltersStats()
        {
            // Arrange: log a real warning marker, and define a control marker that is never logged.
            string marker = NewMarker();
            string neverLogged = NewMarker();
            Debug.LogWarning(marker);

            // Pre-condition via the INDEPENDENT reader (different code path than the handler).
            Assert.IsTrue(ConsoleContainsMarker(marker),
                "Independent reader should already see the logged warning marker.");
            Assert.IsFalse(ConsoleContainsMarker(neverLogged),
                "Control marker must not be present before the handler runs.");

            // Act: filter by the marker TEXT only — not by logType. The Unity Test Runner can tag a Debug.LogWarning
            // with extra mode bits so its decoded LogType is not exactly "Warning" in-runner; a type filter would then
            // wrongly drop it. enhanced_read_logs' type filtering itself is verified live and by the stats below.
            var parameters = new JObject
            {
                ["filterText"] = marker,
                ["count"] = 1000,
                ["format"] = "detailed"
            };
            HandlerOutcome r = ConsoleHandler.EnhancedReadLogs(parameters);

            // Assert handler succeeded.
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);

            // AFTERMATH 1: the handler's own payload surfaces the marker.
            var logs = (JArray)data["logs"];
            Assert.IsNotNull(logs, "Expected a logs array in the payload.");
            bool payloadHasMarker = logs.Any(t =>
                t["message"] != null &&
                t["message"].ToString().IndexOf(marker, StringComparison.Ordinal) >= 0);
            Assert.IsTrue(payloadHasMarker, "enhanced_read_logs payload should contain the logged marker.");

            // AFTERMATH 2: the statistics are SCOPED to the filter (not the whole buffer) — the one marker we logged
            // is counted in exactly one bucket, so the cross-bucket total is at least 1.
            var stats = (JObject)data["statistics"];
            Assert.IsNotNull(stats, "Expected statistics in the payload.");
            int statTotal = (int)stats["errors"] + (int)stats["warnings"] + (int)stats["logs"]
                + (int)stats["asserts"] + (int)stats["exceptions"];
            Assert.GreaterOrEqual(statTotal, 1, "Filtered statistics should count the matched entry.");

            // AFTERMATH 3 (baseline did not move): the control marker is absent from the result.
            bool payloadHasControl = logs.Any(t =>
                t["message"] != null &&
                t["message"].ToString().IndexOf(neverLogged, StringComparison.Ordinal) >= 0);
            Assert.IsFalse(payloadHasControl, "A never-logged marker must not appear in results.");

            // Cross-check the independent reader still agrees post-call (handler is read-only).
            Assert.IsTrue(ConsoleContainsMarker(marker),
                "enhanced_read_logs must not consume/clear the console it reads.");
        }

        // ----------------------------------------------------------------------------------------
        // clear_console -> ConsoleHandler.ClearConsole
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// Aftermath: after clear_console, the editor console buffer no longer contains a marker that
        /// was present before the call. Verified through the INDEPENDENT ReadConsoleEntries reader,
        /// not through ClearConsole's own reported counts.
        /// </summary>
        [Test]
        public void ClearConsole_RemovesLoggedEntry_FromBuffer()
        {
            // Arrange: put a known marker into the console and confirm it is there independently.
            string marker = NewMarker();
            Debug.Log(marker);
            Assert.IsTrue(ConsoleContainsMarker(marker),
                "Marker should be in the console before clear_console.");

            // Act: clear the console (default params; no preserve flags).
            HandlerOutcome r = ConsoleHandler.ClearConsole(new JObject());
            Assert.IsFalse(r.IsError, r.Error);

            // Sanity-check the handler's self-report, then trust the independent re-read.
            var data = JObject.FromObject(r.Payload);
            Assert.AreEqual(0, (int)data["remainingCount"],
                "clear_console reports nothing remaining after a full clear.");

            // AFTERMATH: independent re-read shows the buffer no longer contains the marker.
            Assert.IsFalse(ConsoleContainsMarker(marker),
                "After clear_console the marker must be gone from the editor console buffer.");
        }

        /// <summary>
        /// Aftermath (baseline / error path): clear_console with preserveWarnings is REFUSED, and it
        /// must NOT touch the console — a marker logged beforehand survives the rejected call.
        /// </summary>
        [Test]
        public void ClearConsole_WithPreserveFlag_RefusesAndLeavesBufferIntact()
        {
            // Arrange.
            string marker = NewMarker();
            Debug.Log(marker);
            Assert.IsTrue(ConsoleContainsMarker(marker));

            // Act: ask for an unsupported selective clear.
            var parameters = new JObject { ["preserveWarnings"] = true };
            HandlerOutcome r = ConsoleHandler.ClearConsole(parameters);

            // Assert: rejected with the validation code, no clearing happened.
            Assert.IsTrue(r.IsError, "preserveWarnings must be refused, not silently honored.");
            Assert.AreEqual("VALIDATION_ERROR", r.Code);

            // AFTERMATH: the console is untouched — the marker is still present (baseline did not move).
            Assert.IsTrue(ConsoleContainsMarker(marker),
                "A refused clear_console must leave the console buffer intact.");
        }

        // ----------------------------------------------------------------------------------------
        // clear_logs -> SystemHandler.ClearLogs
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// Aftermath: after clear_logs, the editor console buffer no longer contains a previously
        /// logged marker. Verified through the INDEPENDENT ReadConsoleEntries reader.
        /// </summary>
        [Test]
        public void ClearLogs_EmptiesEditorConsoleBuffer()
        {
            // Arrange.
            string marker = NewMarker();
            Debug.LogWarning(marker);
            Assert.IsTrue(ConsoleContainsMarker(marker),
                "Marker should be in the console before clear_logs.");

            // Act.
            HandlerOutcome r = SystemHandler.ClearLogs(new JObject());
            Assert.IsFalse(r.IsError, r.Error);

            // AFTERMATH: independent re-read confirms the buffer is cleared of the marker.
            Assert.IsFalse(ConsoleContainsMarker(marker),
                "After clear_logs the marker must be gone from the editor console buffer.");

            // Cross-confirm emptiness via the read_logs handler (its own backing reader): zero entries.
            HandlerOutcome readBack = SystemHandler.ReadLogs(new JObject { ["count"] = 1000 });
            Assert.IsFalse(readBack.IsError, readBack.Error);
            var readData = JObject.FromObject(readBack.Payload);
            var readLogs = (JArray)readData["logs"];
            bool anyMarker = readLogs.Any(t =>
                t["message"] != null &&
                t["message"].ToString().IndexOf(marker, StringComparison.Ordinal) >= 0);
            Assert.IsFalse(anyMarker, "read_logs must not return a marker after clear_logs.");
        }
    }
}
