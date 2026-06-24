using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles Unity Editor console operations including clearing and enhanced log reading
    /// </summary>
    public static class ConsoleHandler
    {
        // Reflection members for accessing internal LogEntry data
        private static MethodInfo _startGettingEntriesMethod;
        private static MethodInfo _endGettingEntriesMethod;
        private static MethodInfo _clearMethod;
        private static MethodInfo _getCountMethod;
        private static MethodInfo _getEntryMethod;
        private static FieldInfo _modeField;
        private static FieldInfo _messageField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;
        private static FieldInfo _instanceIdField;

        // Mode bits for log type detection
        private const int ModeBitError = 1 << 0;
        private const int ModeBitAssert = 1 << 1;
        private const int ModeBitWarning = 1 << 2;
        private const int ModeBitLog = 1 << 3;
        private const int ModeBitException = 1 << 4;
        private const int ModeBitScriptingError = 1 << 9;
        private const int ModeBitScriptingWarning = 1 << 10;
        private const int ModeBitScriptingLog = 1 << 11;
        private const int ModeBitScriptingException = 1 << 18;
        private const int ModeBitScriptingAssertion = 1 << 22;

        static ConsoleHandler()
        {
            InitializeReflection();
        }

        /// <summary>
        /// Initialize reflection members for accessing Unity's internal console APIs
        /// </summary>
        private static void InitializeReflection()
        {
            try
            {
                Type logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType == null)
                {
                    Debug.LogError("[ConsoleHandler] Could not find internal type UnityEditor.LogEntries");
                    return;
                }

                BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _startGettingEntriesMethod = logEntriesType.GetMethod("StartGettingEntries", staticFlags);
                _endGettingEntriesMethod = logEntriesType.GetMethod("EndGettingEntries", staticFlags);
                _clearMethod = logEntriesType.GetMethod("Clear", staticFlags);
                _getCountMethod = logEntriesType.GetMethod("GetCount", staticFlags);
                _getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", staticFlags);

                Type logEntryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry");
                if (logEntryType == null)
                {
                    Debug.LogError("[ConsoleHandler] Could not find internal type UnityEditor.LogEntry");
                    return;
                }

                _modeField = logEntryType.GetField("mode", instanceFlags);
                _messageField = logEntryType.GetField("message", instanceFlags);
                _fileField = logEntryType.GetField("file", instanceFlags);
                _lineField = logEntryType.GetField("line", instanceFlags);
                _instanceIdField = logEntryType.GetField("instanceID", instanceFlags);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConsoleHandler] Failed to initialize reflection: {ex}");
            }
        }

        /// <summary>
        /// Clears the Unity console
        /// </summary>
        /// <param name="parameters">Command parameters</param>
        /// <returns>Clear result</returns>
        public static HandlerOutcome ClearConsole(JObject parameters)
        {
            try
            {
                if (_clearMethod == null)
                {
                    return HandlerOutcome.Fail("Console reflection not initialized properly", "INVALID_STATE");
                }

                // Extract parameters
                bool clearOnPlay = parameters["clearOnPlay"]?.ToObject<bool>() ?? true;
                bool clearOnRecompile = parameters["clearOnRecompile"]?.ToObject<bool>() ?? true;
                bool clearOnBuild = parameters["clearOnBuild"]?.ToObject<bool>() ?? true;
                bool preserveWarnings = parameters["preserveWarnings"]?.ToObject<bool>() ?? false;
                bool preserveErrors = parameters["preserveErrors"]?.ToObject<bool>() ?? false;

                // Count logs before clearing
                int totalBefore = _getCountMethod != null ? (int)_getCountMethod.Invoke(null, null) : 0;
                int clearedCount = totalBefore;
                int remainingCount = 0;

                // Unity's console clear is all-or-nothing — there is no native selective clear. The old code
                // logged a warning and then cleared EVERYTHING anyway (silently destroying the errors/warnings
                // the caller asked to keep). Refuse instead, so callers aren't misled into losing data.
                if (preserveWarnings || preserveErrors)
                {
                    return HandlerOutcome.Fail("preserveWarnings/preserveErrors are not supported — Unity's console API has no selective clear. Omit them to clear everything, or read/filter with enhanced_read_logs first.", "VALIDATION_ERROR");
                }

                // Clear the console
                _clearMethod.Invoke(null, null);

                // Update console preferences if requested
                bool settingsUpdated = false;
                if (clearOnPlay != EditorPrefs.GetBool("ClearOnPlay", true))
                {
                    EditorPrefs.SetBool("ClearOnPlay", clearOnPlay);
                    settingsUpdated = true;
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    message = "Console cleared successfully",
                    clearedCount = clearedCount,
                    remainingCount = remainingCount,
                    settingsUpdated = settingsUpdated,
                    clearOnPlay = clearOnPlay,
                    clearOnRecompile = clearOnRecompile,
                    clearOnBuild = clearOnBuild,
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConsoleHandler] Error clearing console: {ex}");
                return HandlerOutcome.Fail($"Failed to clear console: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads console logs with enhanced filtering
        /// </summary>
        /// <param name="parameters">Command parameters</param>
        /// <returns>Filtered logs</returns>
        public static HandlerOutcome EnhancedReadLogs(JObject parameters)
        {
            try
            {
                if (!IsReflectionInitialized())
                {
                    return HandlerOutcome.Fail("Console reflection not initialized properly", "INVALID_STATE");
                }

                // Extract parameters
                int count = parameters["count"]?.ToObject<int>() ?? 100;
                // Accept logTypes (array) or logType (singular string) — the singular was silently ignored before.
                var logTypes = (parameters["logTypes"] as JArray)?.Select(t => t.ToString()).ToList()
                    ?? (parameters["logType"] != null ? new List<string> { parameters["logType"].ToString() } : new List<string> { "All" });
                string filterText = parameters["filterText"]?.ToString();
                bool includeStackTrace = parameters["includeStackTrace"]?.ToObject<bool>() ?? true;
                string format = parameters["format"]?.ToString() ?? "detailed";
                string sinceTimestamp = parameters["sinceTimestamp"]?.ToString();
                string untilTimestamp = parameters["untilTimestamp"]?.ToString();
                string sortOrder = parameters["sortOrder"]?.ToString() ?? "newest";
                string groupBy = parameters["groupBy"]?.ToString() ?? "none";

                // Expand "All" to all types
                if (logTypes.Contains("All"))
                {
                    logTypes = new List<string> { "Log", "Warning", "Error", "Assert", "Exception" };
                }

                // Parse timestamps
                DateTime? sinceTime = null;
                DateTime? untilTime = null;
                if (!string.IsNullOrEmpty(sinceTimestamp))
                {
                    sinceTime = DateTime.Parse(sinceTimestamp);
                }
                if (!string.IsNullOrEmpty(untilTimestamp))
                {
                    untilTime = DateTime.Parse(untilTimestamp);
                }

                // Collect logs
                var logs = new List<object>();
                var statistics = new Dictionary<string, int>
                {
                    { "errors", 0 },
                    { "warnings", 0 },
                    { "logs", 0 },
                    { "asserts", 0 },
                    { "exceptions", 0 }
                };

                _startGettingEntriesMethod.Invoke(null, null);
                try
                {
                    int totalEntries = (int)_getCountMethod.Invoke(null, null);
                    Type logEntryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry");
                    object logEntryInstance = Activator.CreateInstance(logEntryType);

                    // Process entries (newest first by default)
                    for (int i = totalEntries - 1; i >= 0 && logs.Count < count; i--)
                    {
                        _getEntryMethod.Invoke(null, new object[] { i, logEntryInstance });

                        // Extract log data
                        int mode = (int)_modeField.GetValue(logEntryInstance);
                        string message = (string)_messageField.GetValue(logEntryInstance);
                        string file = (string)_fileField.GetValue(logEntryInstance);
                        int line = (int)_lineField.GetValue(logEntryInstance);

                        if (string.IsNullOrEmpty(message))
                            continue;

                        // Determine log type
                        LogType logType = GetLogTypeFromMode(mode);
                        string logTypeString = logType.ToString();

                        // Filter by type
                        if (!logTypes.Contains(logTypeString))
                            continue;

                        // Filter by text
                        if (!string.IsNullOrEmpty(filterText) &&
                            message.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        // Update statistics AFTER the filters, so they describe the FILTERED set — they used to be
                        // incremented before filtering, reporting a misleading whole-buffer tally.
                        switch (logType)
                        {
                            case LogType.Error: statistics["errors"]++; break;
                            case LogType.Warning: statistics["warnings"]++; break;
                            case LogType.Log: statistics["logs"]++; break;
                            case LogType.Assert: statistics["asserts"]++; break;
                            case LogType.Exception: statistics["exceptions"]++; break;
                        }

                        // Extract stack trace if present
                        string stackTrace = null;
                        if (includeStackTrace)
                        {
                            stackTrace = ExtractStackTrace(message);
                            if (!string.IsNullOrEmpty(stackTrace))
                            {
                                message = message.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)[0];
                            }
                        }

                        // Create log entry based on format
                        object logEntry = CreateLogEntry(message, stackTrace, logTypeString, file, line, format);
                        logs.Add(logEntry);
                    }

                    // Reverse if oldest first
                    if (sortOrder == "oldest")
                    {
                        logs.Reverse();
                    }
                }
                finally
                {
                    _endGettingEntriesMethod.Invoke(null, null);
                }

                // Group logs if requested
                object result;
                if (groupBy != "none")
                {
                    var groupedLogs = GroupLogs(logs, groupBy);
                    result = new
                    {
                        success = true,
                        groupedLogs = groupedLogs,
                        count = logs.Count,
                        totalCaptured = (int)_getCountMethod.Invoke(null, null),
                        statistics = statistics,
                        groupBy = groupBy
                    };
                }
                else
                {
                    result = new
                    {
                        success = true,
                        logs = logs,
                        count = logs.Count,
                        totalCaptured = (int)_getCountMethod.Invoke(null, null),
                        statistics = statistics
                    };
                }

                return HandlerOutcome.Ok(result);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConsoleHandler] Error reading logs: {ex}");
                return HandlerOutcome.Fail($"Failed to read logs: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a log entry object based on the specified format
        /// </summary>
        private static object CreateLogEntry(string message, string stackTrace, string logType, string file, int line, string format)
        {
            switch (format)
            {
                case "compact":
                    return new
                    {
                        message = message,
                        logType = logType,
                        formattedCompact = $"[{logType}] {message}"
                    };

                case "plain":
                    return message;

                case "json":
                case "detailed":
                default:
                    var entry = new Dictionary<string, object>
                    {
                        { "message", message },
                        { "logType", logType },
                        { "file", file },
                        { "line", line },
                        { "timestamp", DateTime.UtcNow.ToString("o") }
                    };
                    if (!string.IsNullOrEmpty(stackTrace))
                    {
                        entry["stackTrace"] = stackTrace;
                    }
                    return entry;
            }
        }

        /// <summary>
        /// Groups logs by specified criteria
        /// </summary>
        private static Dictionary<string, List<object>> GroupLogs(List<object> logs, string groupBy)
        {
            var grouped = new Dictionary<string, List<object>>();

            foreach (var log in logs)
            {
                string key = "unknown";
                
                // Entries may be a Dictionary (detailed/json format) OR an anonymous object (compact format) —
                // normalize via JObject so the key is found regardless of shape. The old Dictionary-only check
                // silently bucketed every compact-format entry under "unknown".
                Newtonsoft.Json.Linq.JObject jo = null;
                try { jo = log as Newtonsoft.Json.Linq.JObject ?? Newtonsoft.Json.Linq.JObject.FromObject(log); } catch { jo = null; }
                if (jo != null)
                {
                    switch (groupBy)
                    {
                        case "type":
                            key = jo["logType"]?.ToString() ?? "unknown";
                            break;
                        case "file":
                            key = jo["file"]?.ToString() ?? "unknown";
                            break;
                        case "time":
                            if (DateTime.TryParse(jo["timestamp"]?.ToString(), out DateTime time))
                                key = time.ToString("yyyy-MM-dd HH:00");
                            break;
                    }
                }

                if (!grouped.ContainsKey(key))
                {
                    grouped[key] = new List<object>();
                }
                grouped[key].Add(log);
            }

            return grouped;
        }

        /// <summary>
        /// Checks if reflection is properly initialized
        /// </summary>
        private static bool IsReflectionInitialized()
        {
            return _startGettingEntriesMethod != null &&
                   _endGettingEntriesMethod != null &&
                   _clearMethod != null &&
                   _getCountMethod != null &&
                   _getEntryMethod != null &&
                   _modeField != null &&
                   _messageField != null;
        }

        /// <summary>
        /// Gets LogType from mode bits
        /// </summary>
        private static LogType GetLogTypeFromMode(int mode)
        {
            if ((mode & (ModeBitError | ModeBitScriptingError | ModeBitException | ModeBitScriptingException)) != 0)
            {
                return LogType.Error;
            }
            else if ((mode & (ModeBitAssert | ModeBitScriptingAssertion)) != 0)
            {
                return LogType.Assert;
            }
            else if ((mode & (ModeBitWarning | ModeBitScriptingWarning)) != 0)
            {
                return LogType.Warning;
            }
            else if ((mode & ModeBitException) != 0)
            {
                return LogType.Exception;
            }
            else
            {
                return LogType.Log;
            }
        }

        /// <summary>
        /// Extracts stack trace from a log message
        /// </summary>
        private static string ExtractStackTrace(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage))
                return null;

            string[] lines = fullMessage.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length <= 1)
                return null;

            int stackStartIndex = -1;
            for (int i = 1; i < lines.Length; ++i)
            {
                string trimmedLine = lines[i].TrimStart();
                if (trimmedLine.StartsWith("at ") ||
                    trimmedLine.StartsWith("UnityEngine.") ||
                    trimmedLine.StartsWith("UnityEditor.") ||
                    trimmedLine.Contains("(at "))
                {
                    stackStartIndex = i;
                    break;
                }
            }

            if (stackStartIndex > 0)
            {
                return string.Join("\n", lines.Skip(stackStartIndex));
            }

            return null;
        }
    }
}