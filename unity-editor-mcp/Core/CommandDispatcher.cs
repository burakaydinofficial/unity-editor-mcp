using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Core
{
    /// <summary>Logging seam so Core never references UnityEngine.Debug.</summary>
    public interface IMcpLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    /// <summary>A logger that discards everything (the default).</summary>
    public sealed class NullMcpLogger : IMcpLogger
    {
        public static readonly NullMcpLogger Instance = new NullMcpLogger();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    /// <summary>
    /// Maps command-type strings to handlers and turns requests into results.
    /// Unity-independent: the Unity layer registers handlers (which may call
    /// UnityEditor); the dispatcher itself never does. An unknown command or a
    /// throwing handler yields a proper error result — errors are never laundered
    /// into successes.
    /// </summary>
    public sealed class CommandDispatcher
    {
        private readonly Dictionary<string, Func<JObject, HandlerOutcome>> _handlers =
            new Dictionary<string, Func<JObject, HandlerOutcome>>(StringComparer.OrdinalIgnoreCase);
        private readonly IMcpLogger _log;
        private readonly HashSet<string> _requiresConfirm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Func<CommandRequest, HandlerOutcome> _fallback;

        public CommandDispatcher(IMcpLogger log = null)
        {
            _log = log ?? NullMcpLogger.Instance;
        }

        /// <summary>Number of registered handlers.</summary>
        public int Count => _handlers.Count;

        /// <summary>The command types that have a registered handler.</summary>
        public IEnumerable<string> RegisteredTypes => _handlers.Keys;

        /// <summary>True if a handler is registered for the given command type.</summary>
        public bool IsRegistered(string type) => type != null && _handlers.ContainsKey(type);

        /// <summary>Registers a handler. Throws on an empty type or a duplicate.</summary>
        public void Register(string type, Func<JObject, HandlerOutcome> handler) => Register(type, handler, false);

        /// <summary>Registers a handler; <paramref name="requiresConfirm"/> marks an irreversible/coarse op that
        /// the dispatcher refuses (CONFIRMATION_REQUIRED) unless the request carries params.confirm==true (H3).
        /// Ops with a finer safety model (CAS serialization writes, the dependents-aware asset delete) are NOT
        /// marked — they self-gate.</summary>
        public void Register(string type, Func<JObject, HandlerOutcome> handler, bool requiresConfirm)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("type is required", nameof(type));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (_handlers.ContainsKey(type)) throw new InvalidOperationException($"Duplicate handler for '{type}'");
            _handlers[type] = handler;
            if (requiresConfirm) _requiresConfirm.Add(type);
        }

        /// <summary>True if the command type is gated behind params.confirm==true (H3).</summary>
        public bool RequiresConfirm(string type) => type != null && _requiresConfirm.Contains(type);

        /// <summary>
        /// Sets a fallback invoked when no specific handler is registered for a
        /// command type. The strangler hook for the bootstrap migration: the legacy
        /// dispatch switch can be the fallback while handlers move to explicit
        /// registration one category at a time. Pass null to clear.
        /// </summary>
        public void SetFallback(Func<CommandRequest, HandlerOutcome> fallback) => _fallback = fallback;

        /// <summary>Dispatches a request to its handler, always returning a result.</summary>
        public CommandResult Dispatch(CommandRequest request)
        {
            if (request == null)
                return CommandResult.FromOutcome(null, HandlerOutcome.Fail("Null request", "PARSE_ERROR"));

            if (string.IsNullOrEmpty(request.Type))
                return CommandResult.FromOutcome(request.Id, HandlerOutcome.Fail("Missing command type", "PARSE_ERROR"));

            // GraphQL-style field selection: the reserved "fields" meta-param trims the success payload.
            // Applied UNIFORMLY to the registered-handler AND fallback paths — handlers never see it
            // (stripped), and errors are returned unprojected.
            var fields = ExtractFields(request.Params);

            HandlerOutcome outcome;
            if (!_handlers.TryGetValue(request.Type, out var handler))
            {
                if (_fallback == null)
                {
                    _log.Warn($"Unknown command type: {request.Type}");
                    return CommandResult.FromOutcome(request.Id,
                        HandlerOutcome.Fail($"Unknown command type: {request.Type}", "UNKNOWN_COMMAND"));
                }
                try
                {
                    outcome = _fallback(fields == null ? request : StripFieldsFromRequest(request));
                }
                catch (Exception ex)
                {
                    _log.Error($"Fallback for '{request.Type}' threw: {ex}");
                    return CommandResult.FromOutcome(request.Id,
                        HandlerOutcome.Fail($"Internal error: {ex.Message}", "INTERNAL_ERROR", details: new { type = request.Type }));
                }
            }
            else
            {
                // H3: irreversible/coarse destructive ops require an explicit confirm:true. The gate is central
                // (here) so it can't be forgotten per-handler; confirm stays in params (some handlers read it).
                if (_requiresConfirm.Contains(request.Type) && !IsConfirmed(request.Params))
                    return CommandResult.FromOutcome(request.Id, HandlerOutcome.Fail(
                        $"'{request.Type}' is destructive and requires confirm:true. Re-send with \"confirm\": true to proceed.",
                        "CONFIRMATION_REQUIRED"));
                try
                {
                    var rawParams = request.Params ?? new JObject();
                    outcome = handler(fields == null ? rawParams : StripFields(rawParams));
                }
                catch (Exception ex)
                {
                    _log.Error($"Handler '{request.Type}' threw: {ex}");
                    return CommandResult.FromOutcome(request.Id,
                        HandlerOutcome.Fail($"Internal error: {ex.Message}", "INTERNAL_ERROR", details: new { type = request.Type }));
                }
            }

            if (fields != null && outcome != null && !outcome.IsError && outcome.Payload != null)
                outcome = ProjectPayload(outcome, fields);
            return CommandResult.FromOutcome(request.Id, outcome);
        }

        // The reserved "confirm" meta-param: true gates an irreversible op (H3). Left in params (not stripped)
        // because some handlers also read it for their own finer gate (e.g. the asset-delete dependents check).
        private static bool IsConfirmed(JObject p)
        {
            var c = p?["confirm"];
            return c != null && c.Type == JTokenType.Boolean && (bool)c;
        }

        // The reserved "fields" meta-param: a string[] of dot-paths selecting which result fields to
        // return (GraphQL-style). Null when absent or not a non-empty string array (→ full payload).
        private static List<string> ExtractFields(JObject p)
        {
            if (p == null || !(p["fields"] is JArray arr) || arr.Count == 0) return null;
            var list = new List<string>();
            foreach (var t in arr)
                if (t.Type == JTokenType.String) list.Add((string)t);
            return list.Count > 0 ? list : null;
        }

        // Hand the handler its params WITHOUT the meta-param, so "fields" never collides with a real param.
        private static JObject StripFields(JObject p)
        {
            var clone = (JObject)p.DeepClone();
            clone.Remove("fields");
            return clone;
        }

        // Same, for the fallback path (which takes a whole CommandRequest).
        private static CommandRequest StripFieldsFromRequest(CommandRequest r) =>
            new CommandRequest { Id = r.Id, Type = r.Type, Params = r.Params == null ? null : StripFields(r.Params) };

        private static HandlerOutcome ProjectPayload(HandlerOutcome outcome, List<string> fields)
        {
            try
            {
                var token = outcome.Payload as JToken ?? JToken.FromObject(outcome.Payload);
                return HandlerOutcome.Ok(FieldProjection.Project(token, fields));
            }
            catch
            {
                // Non-serializable payload — leave it untouched; CommandResult.ToJson's SafeToken handles it.
                return outcome;
            }
        }
    }
}
