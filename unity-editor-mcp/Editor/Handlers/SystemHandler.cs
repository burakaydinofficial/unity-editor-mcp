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

                // Read the editor console (LogEntries) — the same buffer enhanced_read_logs uses — not the legacy
                // LogCapture buffer, which reset on every domain reload and missed editor-internal logs (read_logs
                // returned ~1 entry next to enhanced_read_logs' full console). The editor console exposes no
                // per-entry wall-clock via this reflection, so `timestamp` is the read time (best-effort).
                var entries = ConsoleHandler.ReadConsoleEntries(count, filterType);
                if (entries == null)
                    return HandlerOutcome.Fail("Console reflection not available", "INVALID_STATE");
                string readAt = DateTime.UtcNow.ToString("o");
                var logData = new List<object>();
                foreach (var e in entries)
                {
                    logData.Add(new
                    {
                        message = e["message"],
                        stackTrace = e["stackTrace"],
                        logType = e["logType"],
                        timestamp = readAt
                    });
                }

                return HandlerOutcome.Ok(new
                {
                    logs = logData,
                    count = logData.Count,
                    totalCaptured = logData.Count
                });
            }
            catch (Exception e) { return HandlerOutcome.Fail($"Error reading logs: {e.Message}"); }
        }

        public static HandlerOutcome ClearLogs(JObject parameters)
        {
            try
            {
                // Clear the editor console (LogEntries) — the buffer read_logs now reads — so clear_logs and
                // read_logs stay consistent; also clear the legacy LogCapture buffer.
                ConsoleHandler.ClearConsoleEntries();
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
