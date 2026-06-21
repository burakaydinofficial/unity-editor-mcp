using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>Reads/clears the H5 mutation audit log via the Unity-independent Core AuditLog.</summary>
    public static class AuditLogHandler
    {
        public static HandlerOutcome GetAuditLog(JObject p)
        {
            try
            {
                var entries = AuditLog.Read(AuditLogBridge.Path,
                    p["max"]?.ToObject<int?>() ?? 100, p["type"]?.ToString(), p["since"]?.ToString());
                return HandlerOutcome.Ok(new { count = entries.Count, entries });
            }
            catch (System.Exception e) { return HandlerOutcome.Fail($"get_audit_log failed: {e.Message}"); }
        }

        public static HandlerOutcome ClearAuditLog(JObject p)
        {
            try { AuditLog.Clear(AuditLogBridge.Path); return HandlerOutcome.Ok(new { cleared = true }); }
            catch (System.Exception e) { return HandlerOutcome.Fail($"clear_audit_log failed: {e.Message}"); }
        }
    }
}
