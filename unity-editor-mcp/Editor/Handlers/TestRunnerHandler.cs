using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using ApiTestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;
using UnityEditorMCP.Helpers;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles Unity Test Runner operations for executing and managing tests
    /// </summary>
    public static class TestRunnerHandler
    {
        private static TestRunnerApi testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
        private static TestRunCallback currentCallback;
        private static Dictionary<string, TestResult> lastTestResults = new Dictionary<string, TestResult>();
        private static bool isRunningTests = false;
        private const ApiTestMode AllTestModes = ApiTestMode.EditMode | ApiTestMode.PlayMode;

        /// <summary>
        /// Lists all available tests in the project
        /// </summary>
        public static object ListTests(JObject parameters)
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

                return new
                {
                    tests = tests.ToArray(),
                    totalCount = tests.Count,
                    testMode = testMode.ToString(),
                    message = $"Found {tests.Count} tests"
                };
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to list tests: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs specified tests or all tests
        /// </summary>
        public static object RunTests(JObject parameters)
        {
            try
            {
                if (isRunningTests)
                {
                    return Response.Error("Tests are already running. Please wait for them to complete or cancel.");
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
                        return new
                        {
                            message = "No tests matched the requested filters",
                            testMode = testMode.ToString(),
                            testCount = 0,
                            runAll = runAll
                        };
                    }
                }

                // Create filter for test execution
                var filter = new Filter()
                {
                    testMode = testMode,
                    testNames = filterTestNames,
                    categoryNames = filterCategoryNames
                };

                // Clear previous results
                lastTestResults.Clear();

                // Create callback handler
                if (currentCallback == null)
                {
                    currentCallback = new TestRunCallback();
                    testRunnerApi.RegisterCallbacks(currentCallback);
                }

                isRunningTests = true;

                // Execute tests
                var executionSettings = new ExecutionSettings(filter);
                testRunnerApi.Execute(executionSettings);

                return new
                {
                    message = "Test execution started",
                    testMode = testMode.ToString(),
                    testCount = testNames?.Length ?? 0,
                    runAll = runAll,
                    timestamp = DateTime.UtcNow.ToString("o")
                };
            }
            catch (Exception ex)
            {
                isRunningTests = false;
                return Response.Error($"Failed to run tests: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the results of the last test run
        /// </summary>
        public static object GetTestResults(JObject parameters)
        {
            try
            {
                var includeDetails = parameters["includeDetails"]?.ToObject<bool>() ?? true;
                var filterStatus = parameters["filterStatus"]?.ToString();

                if (lastTestResults.Count == 0)
                {
                    return new
                    {
                        message = "No test results available. Run tests first.",
                        hasResults = false,
                        isRunning = isRunningTests
                    };
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

                return new
                {
                    results = results.ToArray(),
                    summary = summary,
                    isRunning = isRunningTests,
                    totalTests = lastTestResults.Count,
                    message = "Test results retrieved successfully"
                };
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to get test results: {ex.Message}");
            }
        }

        /// <summary>
        /// Cancels currently running tests
        /// </summary>
        public static object CancelTests(JObject parameters)
        {
            try
            {
                if (!isRunningTests)
                {
                    return new
                    {
                        message = "No tests are currently running",
                        wasCancelled = false
                    };
                }

                // Unity doesn't provide a direct way to cancel tests, but we can try to stop the test runner
                EditorApplication.isPlaying = false;
                isRunningTests = false;

                return new
                {
                    message = "Test cancellation requested",
                    wasCancelled = true,
                    timestamp = DateTime.UtcNow.ToString("o")
                };
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to cancel tests: {ex.Message}");
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
                lastTestResults.Clear();
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                Debug.Log($"[TestRunner] Test run completed");
                ProcessTestResults(result);
                isRunningTests = false;
            }

            public void TestStarted(ITestAdaptor test)
            {
                Debug.Log($"[TestRunner] Test started: {test.FullName}");
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                Debug.Log($"[TestRunner] Test finished: {result.Test.FullName} - {result.TestStatus}");

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
