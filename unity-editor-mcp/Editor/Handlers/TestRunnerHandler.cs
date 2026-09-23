using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using ApiTestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;
using UnityEditorMCP.Core;
using UnityEditorMCP.Helpers;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles Unity Test Runner operations for executing and managing tests
    /// </summary>
    public static class TestRunnerHandler
    {
        private static TestRunnerApi _testRunnerApi;
        // Lazily created on first use instead of at domain-load static init, so CreateInstance does
        // not run before the Test Runner subsystem that backs the API is ready. (Audit #31.)
        private static TestRunnerApi testRunnerApi =>
            _testRunnerApi != null ? _testRunnerApi : (_testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>());
        private static TestRunCallback currentCallback;
        private static Dictionary<string, TestResult> lastTestResults = new Dictionary<string, TestResult>();
        private const ApiTestMode AllTestModes = ApiTestMode.EditMode | ApiTestMode.PlayMode;

        // Reload-proofing (bug hunt Core-2): PlayMode runs (and pre-run recompiles) trigger DOMAIN RELOADS that wipe
        // every static above — so callbacks vanished mid-run (results never arrived), and the bool re-entrancy guard
        // reset to false, letting a second run_tests Execute() into an active run. Fixes:
        //  - the guard lives in SessionState (survives reloads; clears on editor restart — the right semantics);
        //  - callbacks re-register EVERY domain load via [InitializeOnLoadMethod] (the Unity-documented pattern —
        //    the Test Runner delivers RunFinished in the FINAL domain to whoever is registered there);
        //  - RunFinished journals the full result tree to Library/, and get_test_results falls back to that file
        //    when a later reload has wiped the in-memory dictionary.
        private const string RunningKey = "UnityEditorMCP.TestRunner.IsRunning";
        private const string RunModeKey = "UnityEditorMCP.TestRunner.RunMode";
        private const string RunIdKey = "UnityEditorMCP.TestRunner.RunId"; // tags results with the run that produced them (feedback Symptom 3)
        private static bool IsRunningTests
        {
            get { return SessionState.GetBool(RunningKey, false); }
            set { SessionState.SetBool(RunningKey, value); }
        }

        // Heartbeat (feedback: the persistent "running" latch could stick true — a missed RunFinished, an empty run
        // that fired no callbacks, or an interrupted run — and, being SessionState, outlive the run for the whole
        // editor process, so a later agent saw "running" having started nothing, with no way to clear it). We now
        // record the time of the last Test Runner callback and believe "running" ONLY while the latch is set AND a
        // callback fired recently. A latch with no callback PROGRESS for StaleAfterSeconds is treated as stale and
        // self-heals. Measuring progress (not total duration) means a legitimately long suite — which fires
        // TestStarted/TestFinished throughout — is never falsely cleared, unlike the old wall-clock check.
        private const string LastActivityKey = "UnityEditorMCP.TestRunner.LastActivityUtc";
        private const double StaleAfterSeconds = 300; // 5 min with no callback progress => the running latch is stale

        private static void MarkActivity() => SessionState.SetString(LastActivityKey, DateTime.UtcNow.ToString("o"));

        private static double SecondsSinceActivity()
        {
            var s = SessionState.GetString(LastActivityKey, "");
            return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? (DateTime.UtcNow - t).TotalSeconds : double.MaxValue;
        }

        // The reconciled, self-healing "is a run actually live": the latch is set AND a callback fired recently.
        private static bool IsRunLive => IsRunningTests && SecondsSinceActivity() < StaleAfterSeconds;

        private static string ResultsFilePath
        {
            get
            {
                var projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
                return System.IO.Path.Combine(projectRoot, "Library", "UnityEditorMCP", "last-test-results.json");
            }
        }

        [InitializeOnLoadMethod]
        private static void ReRegisterCallbacksOnLoad()
        {
            // Deferred one tick: creating the TestRunnerApi instance during domain-load static init runs before the
            // Test Runner subsystem is ready (Audit #31); delayCall is after editor init and still far ahead of any
            // RunFinished delivery.
            EditorApplication.delayCall += EnsureCallbacksRegistered;
        }

        private static void EnsureCallbacksRegistered()
        {
            if (currentCallback == null)
            {
                currentCallback = new TestRunCallback();
                testRunnerApi.RegisterCallbacks(currentCallback);
            }
            // Also clear a latched guard when play mode ends without a RunFinished (idempotent -=/+=). (Bug hunt H.)
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // A PlayMode test run cannot outlive play mode: if it was Stopped/crashed so RunFinished never cleared the
            // running guard, clear it on return to edit mode so run_tests isn't wedged. (Bug hunt H.)
            if (state == PlayModeStateChange.EnteredEditMode && IsRunningTests
                && SessionState.GetString(RunModeKey, "").IndexOf("PlayMode", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                IsRunningTests = false;
            }
        }

        /// <summary>
        /// Lists all available tests in the project
        /// </summary>
        public static HandlerOutcome ListTests(JObject parameters)
        {
            try
            {
                var testMode = ParseTestMode(parameters["testMode"]?.ToString());
                var filterPattern = parameters["filter"]?.ToString();
                var includeCategories = parameters["includeCategories"]?.ToObject<string[]>();
                var excludeCategories = parameters["excludeCategories"]?.ToObject<string[]>();

                var tests = DiscoverTests(testMode, filterPattern, includeCategories, excludeCategories)
                    .Select(test => new
                    {
                        name = test.Name,
                        methodName = test.MethodName,
                        className = test.ClassName,
                        assemblyName = test.AssemblyName,
                        testMode = test.TestMode.ToString(),
                        categories = test.Categories,
                        isAsync = test.IsAsync
                    })
                    .Cast<object>()
                    .ToList();

                return HandlerOutcome.Ok(new
                {
                    tests = tests.ToArray(),
                    totalCount = tests.Count,
                    testMode = testMode.ToString(),
                    message = $"Found {tests.Count} tests"
                });
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to list tests: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs specified tests or all tests
        /// </summary>
        public static HandlerOutcome RunTests(JObject parameters)
        {
            try
            {
                // A run started while compilation/asset-import is pending is silently dropped by Unity (no RunFinished
                // fires), which then wedged the latch. Refuse up front and point at the wait primitive. (Feedback Symptom 4.)
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return HandlerOutcome.Fail(
                        "Editor is compiling or importing assets — a test run would be dropped. Wait for it to finish (get_compilation_state with waitForIdle:true) then retry.",
                        "COMPILING");

                // Reconcile the persistent latch against reality: a set-but-STALE flag (no callback PROGRESS for
                // StaleAfterSeconds — a missed RunFinished, an empty run, or an interrupted run) self-heals so it can't
                // wedge run_tests forever. A genuinely live run (recent callbacks) still refuses; `force` overrides.
                if (IsRunningTests)
                {
                    var forceStart = parameters["force"]?.ToObject<bool>() ?? false;
                    if (IsRunLive && !forceStart)
                        return HandlerOutcome.Fail("Tests are already running — wait for completion or call cancel_tests (or pass force:true).", "INVALID_STATE");
                    if (!IsRunLive)
                        Debug.LogWarning($"[TestRunner] Clearing a stale 'running' flag (no callback activity for {SecondsSinceActivity():F0}s) before a new run.");
                    IsRunningTests = false;
                }

                var testMode = ParseTestMode(parameters["testMode"]?.ToString());
                var testNames = parameters["testNames"]?.ToObject<string[]>();
                var runAll = parameters["runAll"]?.ToObject<bool>() ?? false;
                var includeCategories = parameters["includeCategories"]?.ToObject<string[]>();
                var excludeCategories = parameters["excludeCategories"]?.ToObject<string[]>();
                var filterCategoryNames = includeCategories;
                var filterTestNames = testNames;

                // Unity's Test Runner Filter supports category inclusion, but not exclusion.
                // Resolve excluded categories to explicit test names before creating the filter.
                if (excludeCategories != null && excludeCategories.Length > 0)
                {
                    var discoveredTestNames = DiscoverTests(testMode, null, includeCategories, excludeCategories)
                        .Select(test => test.Name)
                        .ToArray();

                    filterTestNames = testNames != null && testNames.Length > 0
                        ? testNames.Intersect(discoveredTestNames).ToArray()
                        : discoveredTestNames;
                    filterCategoryNames = null;

                    if (filterTestNames.Length == 0)
                    {
                        return HandlerOutcome.Ok(new
                        {
                            message = "No tests matched the requested filters",
                            testMode = testMode.ToString(),
                            testCount = 0,
                            runAll = runAll
                        });
                    }
                }

                // Create filter for test execution
                var filter = new Filter()
                {
                    testMode = testMode,
                    testNames = filterTestNames,
                    categoryNames = filterCategoryNames
                };

                // Don't latch a run that will match NOTHING: Unity may fire no RunFinished for an empty run, which would
                // wedge the running flag ("thought tests were running, none started"). Verify at least one test matches
                // up front, using the same nunit-attribute reflection the Test Runner filters against. (Feedback.)
                var candidateTests = DiscoverTests(testMode, null, includeCategories, excludeCategories);
                if (filterTestNames != null && filterTestNames.Length > 0)
                    candidateTests = candidateTests.Where(t => filterTestNames.Contains(t.Name)).ToList();
                if (candidateTests.Count == 0)
                {
                    return HandlerOutcome.Ok(new
                    {
                        message = "No tests matched the requested filters — nothing to run.",
                        testMode = testMode.ToString(),
                        testCount = 0,
                        runAll = runAll
                    });
                }

                // Clear previous results (memory + journal — a new run invalidates the old file)
                lastTestResults.Clear();
                try { System.IO.File.Delete(ResultsFilePath); } catch { /* best effort */ }

                EnsureCallbacksRegistered();

                var runId = Guid.NewGuid().ToString("N").Substring(0, 12);
                IsRunningTests = true;
                MarkActivity(); // start the heartbeat so an immediate poll doesn't read the fresh run as stale
                SessionState.SetString(RunModeKey, testMode.ToString());
                SessionState.SetString(RunIdKey, runId); // tag this run so get_test_results is self-identifying (Symptom 3)

                // Execute tests
                var executionSettings = new ExecutionSettings(filter);
                testRunnerApi.Execute(executionSettings);

                return HandlerOutcome.Ok(new
                {
                    message = "Test execution started",
                    runId = runId,
                    testMode = testMode.ToString(),
                    testCount = candidateTests.Count,
                    runAll = runAll,
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception ex)
            {
                IsRunningTests = false;
                return HandlerOutcome.Fail($"Failed to run tests: {ex.Message}");
            }
        }

        /// <summary>Journal the in-memory results to Library/ so they survive domain reloads. (Core-2)</summary>
        private static void SaveResultsToJournal()
        {
            try
            {
                var arr = new JArray();
                foreach (var kvp in lastTestResults)
                {
                    var r = kvp.Value;
                    arr.Add(new JObject
                    {
                        ["name"] = r.Name,
                        ["status"] = r.Status.ToString(),
                        ["duration"] = r.Duration,
                        ["startTime"] = r.StartTime.ToString("o"),
                        ["endTime"] = r.EndTime.ToString("o"),
                        ["message"] = r.Message,
                        ["stackTrace"] = r.StackTrace,
                        ["output"] = r.Output
                    });
                }
                var doc = new JObject { ["finishedAt"] = DateTime.UtcNow.ToString("o"), ["runId"] = SessionState.GetString(RunIdKey, ""), ["testMode"] = SessionState.GetString(RunModeKey, ""), ["results"] = arr };
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ResultsFilePath));
                System.IO.File.WriteAllText(ResultsFilePath, doc.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestRunner] Could not journal test results: {ex.Message}");
            }
        }

        /// <summary>Rehydrate lastTestResults from the Library/ journal after a reload wiped them. (Core-2)</summary>
        private static void LoadResultsFromJournal()
        {
            try
            {
                if (!System.IO.File.Exists(ResultsFilePath)) return;
                var doc = JObject.Parse(System.IO.File.ReadAllText(ResultsFilePath));
                // Restore the run's identity so get_test_results stays self-tagging even after an editor restart wiped
                // SessionState (the journal survives restart; SessionState does not). (Symptom 3.)
                if (string.IsNullOrEmpty(SessionState.GetString(RunIdKey, "")) && doc["runId"] != null)
                    SessionState.SetString(RunIdKey, doc["runId"].ToString());
                if (string.IsNullOrEmpty(SessionState.GetString(RunModeKey, "")) && doc["testMode"] != null)
                    SessionState.SetString(RunModeKey, doc["testMode"].ToString());
                var arr = doc["results"] as JArray;
                if (arr == null) return;
                foreach (var t in arr)
                {
                    var name = t["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    TestStatus status;
                    try { status = (TestStatus)Enum.Parse(typeof(TestStatus), t["status"]?.ToString() ?? "Inconclusive"); }
                    catch { status = TestStatus.Inconclusive; }
                    lastTestResults[name] = new TestResult
                    {
                        Name = name,
                        Status = status,
                        Duration = t["duration"]?.ToObject<double>() ?? 0,
                        StartTime = t["startTime"]?.ToObject<DateTime>() ?? default(DateTime),
                        EndTime = t["endTime"]?.ToObject<DateTime>() ?? default(DateTime),
                        Message = t["message"]?.ToString(),
                        StackTrace = t["stackTrace"]?.ToString(),
                        Output = t["output"]?.ToString()
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestRunner] Could not load journaled test results: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the results of the last test run
        /// </summary>
        public static HandlerOutcome GetTestResults(JObject parameters)
        {
            try
            {
                var includeDetails = parameters["includeDetails"]?.ToObject<bool>() ?? true;
                var filterStatus = parameters["filterStatus"]?.ToString();

                // Reconcile the persistent latch: report the self-healing "is running" (a stale latch reads false), and
                // heal it so a later run_tests isn't blocked by a flag no live run backs. (Feedback: stale state.)
                bool live = IsRunLive;
                if (IsRunningTests && !live) IsRunningTests = false;

                // A domain reload after RunFinished wipes the in-memory dictionary — fall back to the journal the
                // callback wrote to Library/ so results survive any number of reloads. (Core-2)
                if (lastTestResults.Count == 0)
                    LoadResultsFromJournal();

                // Self-identifying results (Symptom 3): tag with the run's id + mode so a caller can tell whether the
                // stored results are the run it asked about (a different/older run served silently was the reported bug).
                var runId = SessionState.GetString(RunIdKey, "");
                var runMode = SessionState.GetString(RunModeKey, "");
                var expectRunId = parameters["expectRunId"]?.ToString();
                bool runIdMismatch = !string.IsNullOrEmpty(expectRunId) && !string.IsNullOrEmpty(runId) && !expectRunId.Equals(runId, StringComparison.Ordinal);

                if (lastTestResults.Count == 0)
                {
                    return HandlerOutcome.Ok(new
                    {
                        message = live
                            ? "Tests are running — no results have arrived yet. Poll get_test_results again shortly."
                            : "No test results available. Run tests first.",
                        hasResults = false,
                        isRunning = live,
                        runId = runId,
                        testMode = runMode,
                        runIdMismatch = runIdMismatch
                    });
                }

                var results = new List<object>();

                foreach (var kvp in lastTestResults)
                {
                    var result = kvp.Value;

                    // Apply status filter if specified
                    if (!string.IsNullOrEmpty(filterStatus))
                    {
                        if (!result.Status.ToString().Equals(filterStatus, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    var testResult = new
                    {
                        name = kvp.Key,
                        status = result.Status.ToString(),
                        duration = result.Duration,
                        startTime = result.StartTime.ToString("o"),
                        endTime = result.EndTime.ToString("o")
                    };

                    if (includeDetails)
                    {
                        var detailedResult = new
                        {
                            name = kvp.Key,
                            status = result.Status.ToString(),
                            duration = result.Duration,
                            startTime = result.StartTime.ToString("o"),
                            endTime = result.EndTime.ToString("o"),
                            message = result.Message,
                            stackTrace = result.StackTrace,
                            output = result.Output
                        };
                        results.Add(detailedResult);
                    }
                    else
                    {
                        results.Add(testResult);
                    }
                }

                var summary = CalculateTestSummary();

                return HandlerOutcome.Ok(new
                {
                    results = results.ToArray(),
                    summary = summary,
                    isRunning = live,
                    totalTests = lastTestResults.Count,
                    runId = runId,
                    testMode = runMode,
                    runIdMismatch = runIdMismatch,
                    message = runIdMismatch
                        ? "Test results retrieved, but runId != expectRunId — these are a DIFFERENT run's results."
                        : "Test results retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to get test results: {ex.Message}");
            }
        }

        /// <summary>
        /// Cancels currently running tests
        /// </summary>
        public static HandlerOutcome CancelTests(JObject parameters)
        {
            try
            {
                if (!IsRunningTests)
                {
                    return HandlerOutcome.Ok(new
                    {
                        message = "No tests are currently running.",
                        wasCancelled = false
                    });
                }

                var force = parameters["force"]?.ToObject<bool>() ?? false;
                bool live = IsRunLive;
                var runMode = SessionState.GetString(RunModeKey, "");
                bool isPlayMode = runMode.IndexOf("PlayMode", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isPlayMode)
                {
                    // PlayMode run: exiting play mode aborts it. The (always-registered) callback still delivers partial
                    // results; the play-exit hook + RunFinished also settle the latch. (Bug hunt H.)
                    EditorApplication.isPlaying = false;
                    IsRunningTests = false;
                    return HandlerOutcome.Ok(new
                    {
                        message = "Play-mode test run cancelled (exiting play mode).",
                        wasCancelled = true,
                        timestamp = DateTime.UtcNow.ToString("o")
                    });
                }

                // EditMode: Unity has no abort API. If the run is genuinely LIVE (recent callbacks), refuse — clearing
                // the latch would let a second run start concurrently. But if the latch is STALE (no callback progress —
                // a stuck flag no run backs, the reported failure mode), RESET it so run_tests is unblocked. Safe because
                // the callback is permanently registered, so any still-live run's results still arrive. (Feedback: stuck state.)
                if (live && !force)
                    return HandlerOutcome.Fail(
                        "A live EditMode run can't be aborted (Unity has no abort API) — it will finish; poll get_test_results. "
                        + "If the running state is stuck with no active run, re-call with force:true to reset it.", "UNSUPPORTED");

                IsRunningTests = false;
                return HandlerOutcome.Ok(new
                {
                    message = force
                        ? "EditMode run can't be aborted, but the running state was force-reset — run_tests is unblocked. Any live run's results still arrive via the callback."
                        : "Cleared a stale 'running' state (no active EditMode run detected) — run_tests is unblocked.",
                    wasCancelled = false,
                    stateReset = true,
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to cancel tests: {ex.Message}");
            }
        }

        #region Helper Methods

        private static ApiTestMode ParseTestMode(string mode)
        {
            if (string.IsNullOrEmpty(mode))
                return AllTestModes;

            if (mode.Equals("EditAndPlayMode", StringComparison.OrdinalIgnoreCase) ||
                mode.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                return AllTestModes;
            }

            if (Enum.TryParse<ApiTestMode>(mode, true, out ApiTestMode result))
                return result;

            return AllTestModes;
        }

        private static List<DiscoveredTest> DiscoverTests(ApiTestMode testMode, string filterPattern, string[] includeCategories, string[] excludeCategories)
        {
            var tests = new List<DiscoveredTest>();
            var includeSet = CreateStringSet(includeCategories);
            var excludeSet = CreateStringSet(excludeCategories);

            var editorAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetReferencedAssemblies().Any(r => r.Name == "nunit.framework"));

            foreach (var assembly in editorAssemblies)
            {
                try
                {
                    var types = assembly.GetTypes()
                        .Where(type => HasAttribute(type, "NUnit.Framework.TestFixtureAttribute") ||
                                       GetTestMethods(type).Any());

                    foreach (var type in types)
                    {
                        var testMethods = GetTestMethods(type).ToArray();
                        if (testMethods.Length == 0)
                            continue;

                        foreach (var method in testMethods)
                        {
                            var isUnityTest = HasAttribute(method, "UnityEngine.TestTools.UnityTestAttribute");
                            var methodTestMode = isUnityTest ? ApiTestMode.PlayMode : ApiTestMode.EditMode;

                            if ((testMode & methodTestMode) == 0)
                                continue;

                            var testName = $"{type.FullName}.{method.Name}";
                            if (!string.IsNullOrEmpty(filterPattern) && !testName.Contains(filterPattern))
                                continue;

                            var categories = GetCategoryNames(type)
                                .Concat(GetCategoryNames(method))
                                .Distinct()
                                .ToArray();

                            if (includeSet != null && !categories.Any(includeSet.Contains))
                                continue;

                            if (excludeSet != null && categories.Any(excludeSet.Contains))
                                continue;

                            tests.Add(new DiscoveredTest
                            {
                                Name = testName,
                                MethodName = method.Name,
                                ClassName = type.FullName,
                                AssemblyName = assembly.GetName().Name,
                                TestMode = methodTestMode,
                                Categories = categories,
                                IsAsync = isUnityTest
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to process assembly {assembly.GetName().Name}: {ex.Message}");
                }
            }

            return tests;
        }

        private static IEnumerable<MethodInfo> GetTestMethods(Type type)
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => HasAttribute(method, "NUnit.Framework.TestAttribute") ||
                                 HasAttribute(method, "UnityEngine.TestTools.UnityTestAttribute"));
        }

        private static string[] GetCategoryNames(MemberInfo member)
        {
            return member.GetCustomAttributes(true)
                .Where(attribute => attribute.GetType().FullName == "NUnit.Framework.CategoryAttribute")
                .Select(GetCategoryName)
                .Where(category => !string.IsNullOrEmpty(category))
                .ToArray();
        }

        private static string GetCategoryName(object categoryAttribute)
        {
            var type = categoryAttribute.GetType();
            return type.GetProperty("Name")?.GetValue(categoryAttribute, null)?.ToString() ??
                   type.GetProperty("Category")?.GetValue(categoryAttribute, null)?.ToString();
        }

        private static bool HasAttribute(MemberInfo member, string attributeFullName)
        {
            return member.GetCustomAttributes(true)
                .Any(attribute => attribute.GetType().FullName == attributeFullName);
        }

        private static HashSet<string> CreateStringSet(string[] values)
        {
            return values != null && values.Length > 0
                ? new HashSet<string>(values, StringComparer.OrdinalIgnoreCase)
                : null;
        }

        private static object CalculateTestSummary()
        {
            int passed = 0;
            int failed = 0;
            int skipped = 0;
            int inconclusive = 0;
            double totalDuration = 0;

            foreach (var result in lastTestResults.Values)
            {
                totalDuration += result.Duration;

                switch (result.Status)
                {
                    case TestStatus.Passed:
                        passed++;
                        break;
                    case TestStatus.Failed:
                        failed++;
                        break;
                    case TestStatus.Skipped:
                        skipped++;
                        break;
                    case TestStatus.Inconclusive:
                        inconclusive++;
                        break;
                }
            }

            return new
            {
                total = lastTestResults.Count,
                passed = passed,
                failed = failed,
                skipped = skipped,
                inconclusive = inconclusive,
                duration = totalDuration,
                successRate = lastTestResults.Count > 0 ? (passed / (double)lastTestResults.Count) * 100 : 0
            };
        }

        #endregion

        /// <summary>
        /// Internal class to handle test execution callbacks
        /// </summary>
        private class TestRunCallback : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                Debug.Log($"[TestRunner] Starting test run");
                MarkActivity();
                lastTestResults.Clear();
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                Debug.Log($"[TestRunner] Test run completed");
                MarkActivity();
                ProcessTestResults(result);
                // Journal the full result set: a later reload (script edit, play exit) wipes the in-memory
                // dictionary, and get_test_results falls back to this file. (Core-2)
                SaveResultsToJournal();
                IsRunningTests = false;
            }

            public void TestStarted(ITestAdaptor test)
            {
                Debug.Log($"[TestRunner] Test started: {test.FullName}");
                MarkActivity(); // heartbeat: a long single test still refreshes activity at its start
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                Debug.Log($"[TestRunner] Test finished: {result.Test.FullName} - {result.TestStatus}");
                MarkActivity();

                if (result.HasChildren || result.Test == null)
                    return;

                // Store individual test result
                var testResult = new TestResult
                {
                    Name = result.Test.FullName,
                    Status = ConvertTestStatus(result.TestStatus),
                    Duration = result.Duration,
                    StartTime = result.StartTime,
                    EndTime = result.EndTime,
                    Message = result.Message,
                    StackTrace = result.StackTrace,
                    Output = result.Output
                };

                lastTestResults[result.Test.FullName] = testResult;
            }

            private void ProcessTestResults(ITestResultAdaptor result)
            {
                // Process all results recursively
                if (result.HasChildren)
                {
                    foreach (var child in result.Children)
                    {
                        ProcessTestResults(child);
                    }
                }
                else if (result.Test != null)
                {
                    // Leaf node - actual test
                    var testResult = new TestResult
                    {
                        Name = result.Test.FullName,
                        Status = ConvertTestStatus(result.TestStatus),
                        Duration = result.Duration,
                        StartTime = result.StartTime,
                        EndTime = result.EndTime,
                        Message = result.Message,
                        StackTrace = result.StackTrace,
                        Output = result.Output
                    };

                    lastTestResults[result.Test.FullName] = testResult;
                }
            }

            private TestStatus ConvertTestStatus(UnityEditor.TestTools.TestRunner.Api.TestStatus status)
            {
                switch (status)
                {
                    case UnityEditor.TestTools.TestRunner.Api.TestStatus.Passed:
                        return TestStatus.Passed;
                    case UnityEditor.TestTools.TestRunner.Api.TestStatus.Failed:
                        return TestStatus.Failed;
                    case UnityEditor.TestTools.TestRunner.Api.TestStatus.Skipped:
                        return TestStatus.Skipped;
                    case UnityEditor.TestTools.TestRunner.Api.TestStatus.Inconclusive:
                        return TestStatus.Inconclusive;
                    default:
                        return TestStatus.Inconclusive;
                }
            }
        }

        /// <summary>
        /// Internal class to store test results
        /// </summary>
        private class TestResult
        {
            public string Name { get; set; }
            public TestStatus Status { get; set; }
            public double Duration { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public string Output { get; set; }
        }

        private class DiscoveredTest
        {
            public string Name { get; set; }
            public string MethodName { get; set; }
            public string ClassName { get; set; }
            public string AssemblyName { get; set; }
            public ApiTestMode TestMode { get; set; }
            public string[] Categories { get; set; }
            public bool IsAsync { get; set; }
        }

        private enum TestStatus
        {
            Passed,
            Failed,
            Skipped,
            Inconclusive
        }
    }
}
