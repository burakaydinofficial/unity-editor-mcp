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

        public CommandDispatcher(IMcpLogger log = null)
        {
            _log = log ?? NullMcpLogger.Instance;
        }

        /// <summary>Number of registered handlers.</summary>
        public int Count => _handlers.Count;

        /// <summary>True if a handler is registered for the given command type.</summary>
        public bool IsRegistered(string type) => type != null && _handlers.ContainsKey(type);

        /// <summary>Registers a handler. Throws on an empty type or a duplicate.</summary>
        public void Register(string type, Func<JObject, HandlerOutcome> handler)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("type is required", nameof(type));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (_handlers.ContainsKey(type)) throw new InvalidOperationException($"Duplicate handler for '{type}'");
            _handlers[type] = handler;
        }

        /// <summary>Dispatches a request to its handler, always returning a result.</summary>
        public CommandResult Dispatch(CommandRequest request)
        {
            if (request == null)
                return CommandResult.FromOutcome(null, HandlerOutcome.Fail("Null request", "PARSE_ERROR"));

            if (string.IsNullOrEmpty(request.Type))
                return CommandResult.FromOutcome(request.Id, HandlerOutcome.Fail("Missing command type", "PARSE_ERROR"));

            if (!_handlers.TryGetValue(request.Type, out var handler))
            {
                _log.Warn($"Unknown command type: {request.Type}");
                return CommandResult.FromOutcome(request.Id,
                    HandlerOutcome.Fail($"Unknown command type: {request.Type}", "UNKNOWN_COMMAND"));
            }

            try
            {
                var outcome = handler(request.Params ?? new JObject());
                return CommandResult.FromOutcome(request.Id, outcome);
            }
            catch (Exception ex)
            {
                _log.Error($"Handler '{request.Type}' threw: {ex}");
                return CommandResult.FromOutcome(request.Id,
                    HandlerOutcome.Fail($"Internal error: {ex.Message}", "INTERNAL_ERROR", details: new { type = request.Type }));
            }
        }
    }
}
