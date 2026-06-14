using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Core
{
    /// <summary>A command received from the MCP server: { id, type, params }.</summary>
    public sealed class CommandRequest
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("params")] public JObject Params { get; set; }
    }

    /// <summary>
    /// The result of a single handler invocation. A handler returns either
    /// <see cref="Ok"/> or <see cref="Fail"/>; there is deliberately no shape that
    /// lets an error serialize as a success. This is the design fix for the
    /// "errors laundered as success" defect (see protocol/README.md).
    /// </summary>
    public sealed class HandlerOutcome
    {
        public bool IsError { get; private set; }
        public object Payload { get; private set; }
        public string Error { get; private set; }
        public string Code { get; private set; }
        public string Remediation { get; private set; }
        public object Details { get; private set; }

        public static HandlerOutcome Ok(object payload) =>
            new HandlerOutcome { IsError = false, Payload = payload };

        public static HandlerOutcome Fail(string error, string code = "INTERNAL_ERROR", string remediation = null, object details = null) =>
            new HandlerOutcome { IsError = true, Error = error, Code = code, Remediation = remediation, Details = details };
    }

    /// <summary>
    /// A correlated, serializable response built by the dispatcher from a request
    /// id and a <see cref="HandlerOutcome"/>. Serializes to the protocol envelope:
    /// success → { id, status:"success", result }, error → { id, status:"error",
    /// error, code, [remediation], [details] }.
    /// </summary>
    public sealed class CommandResult
    {
        public string Id { get; }
        public bool IsError { get; }
        private readonly object _payload;
        private readonly string _error;
        private readonly string _code;
        private readonly string _remediation;
        private readonly object _details;

        private CommandResult(string id, bool isError, object payload, string error, string code, string remediation, object details)
        {
            Id = id;
            IsError = isError;
            _payload = payload;
            _error = error;
            _code = code;
            _remediation = remediation;
            _details = details;
        }

        public static CommandResult FromOutcome(string id, HandlerOutcome outcome)
        {
            if (outcome == null)
                return new CommandResult(id, true, null, "Handler returned no outcome", "INTERNAL_ERROR", null, null);
            return outcome.IsError
                ? new CommandResult(id, true, null, outcome.Error, outcome.Code, outcome.Remediation, outcome.Details)
                : new CommandResult(id, false, outcome.Payload, null, null, null, null);
        }

        public string ToJson()
        {
            var o = new JObject { ["id"] = Id };
            if (IsError)
            {
                o["status"] = "error";
                o["error"] = _error;
                o["code"] = _code;
                if (_remediation != null) o["remediation"] = _remediation;
                if (_details != null) o["details"] = SafeToken(_details);
            }
            else
            {
                o["status"] = "success";
                // SafeToken: an already-JToken passes through (the common case — handlers return JObject);
                // POCOs/anonymous types go through FromObject; null becomes JSON null.
                o["result"] = SafeToken(_payload);
            }
            return o.ToString(Formatting.None);
        }

        // Converts an arbitrary value to a JToken without ever throwing — a non-serializable payload or
        // details (circular ref, Exception, Type, IntPtr) would otherwise throw out of ToJson() and drop
        // the whole framed reply on the drain path. (Audit finding.)
        private static JToken SafeToken(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is JToken token) return token;
            try { return JToken.FromObject(value); }
            catch (System.Exception ex) { return new JValue($"[unserializable {value.GetType().Name}: {ex.Message}]"); }
        }
    }
}
