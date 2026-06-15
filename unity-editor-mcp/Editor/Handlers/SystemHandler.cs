using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
using UnityEditorMCP.Logging;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Core system commands — ping, read_logs, clear_logs, refresh_assets — on the Core
    /// CommandDispatcher rail (HandlerOutcome). Logic lifted verbatim from the legacy inline
    /// ProcessCommand cases; wire shape unchanged.
    /// </summary>
    public static class SystemHandler
    {
        public static HandlerOutcome Ping(JObject parameters)
        {
            return HandlerOutcome.Ok(new
            {
                message = "pong",
                echo = parameters?["message"]?.ToString(),
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        public static HandlerOutcome ReadLogs(JObject parameters)
        {
            try
            {
                int count = 100;
                string logTypeFilter = null;

                if (parameters != null)
                {
                    if (parameters.ContainsKey("count"))
                    {
                        if (int.TryParse(parameters["count"].ToString(), out int parsedCount))
                        {
                            count = Math.Min(Math.Max(parsedCount, 1), 1000); // Clamp between 1 and 1000
                        }
                    }

                    if (parameters.ContainsKey("logType"))
                    {
                        logTypeFilter = parameters["logType"].ToString();
                    }
                }

                LogType? filterType = null;
                if (!string.IsNullOrEmpty(logTypeFilter))
                {
                    if (Enum.TryParse<LogType>(logTypeFilter, true, out LogType parsed))
                    {
                        filterType = parsed;
                    }
                }

                var logs = LogCapture.GetLogs(count, filterType);
                var logData = new List<object>();

                foreach (var log in logs)
                {
                    logData.Add(new
                    {
                        message = log.message,
                        stackTrace = log.stackTrace,
                        logType = log.logType.ToString(),
                        timestamp = log.timestamp.ToString("o")
                    });
                }

                return HandlerOutcome.Ok(new
                {
                    logs = logData,
                    count = logData.Count,
                    totalCaptured = logs.Count
                });
            }
            catch (Exception e) { return HandlerOutcome.Fail($"Error reading logs: {e.Message}"); }
        }

        public static HandlerOutcome ClearLogs(JObject parameters)
        {
            try
            {
                LogCapture.ClearLogs();
                return HandlerOutcome.Ok(new
                {
                    message = "Logs cleared successfully",
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception e) { return HandlerOutcome.Fail($"Error clearing logs: {e.Message}"); }
        }

        public static HandlerOutcome RefreshAssets(JObject parameters)
        {
            try
            {
                // Trigger Unity to recompile and refresh assets
                AssetDatabase.Refresh();
                bool isCompiling = EditorApplication.isCompiling;
                return HandlerOutcome.Ok(new
                {
                    message = "Asset refresh triggered",
                    isCompiling = isCompiling,
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception e) { return HandlerOutcome.Fail($"Error refreshing assets: {e.Message}"); }
        }
    }
}
