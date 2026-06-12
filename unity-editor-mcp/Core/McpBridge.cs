using System;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// Composes the whole Unity-independent pipeline: a <see cref="TcpTransport"/>
    /// receives framed messages, each is parsed into a <see cref="CommandRequest"/>
    /// and enqueued on a <see cref="CommandQueue"/>; the editor main thread calls
    /// <see cref="Drain"/> to dispatch them via a <see cref="CommandDispatcher"/>
    /// and frame the result back. The Unity layer only has to: construct this,
    /// register handlers (which call UnityEditor), pump <see cref="Drain"/> from
    /// <c>EditorApplication.update</c>, and <see cref="Start"/> it. Fully exercised
    /// over a real loopback socket in <c>dotnet test</c>.
    /// </summary>
    public sealed class McpBridge : IDisposable
    {
        private readonly TcpTransport _transport;
        private readonly CommandQueue _queue;
        private readonly CommandDispatcher _dispatcher;

        public McpBridge(IPAddress bindAddress, int port, IMcpLogger log = null)
        {
            var logger = log ?? NullMcpLogger.Instance;
            _dispatcher = new CommandDispatcher(logger);
            _queue = new CommandQueue(_dispatcher, logger);
            _transport = new TcpTransport(bindAddress, port, logger);
            _transport.MessageReceived += OnMessage;
        }

        /// <summary>The bound port (resolves an ephemeral port-0 request after Start).</summary>
        public int Port => _transport.Port;

        /// <summary>Commands received but not yet drained.</summary>
        public int PendingCount => _queue.Count;

        /// <summary>The underlying dispatcher (for conformance checks, etc.).</summary>
        public CommandDispatcher Dispatcher => _dispatcher;

        /// <summary>Registers a handler for a command type.</summary>
        public void Register(string type, Func<JObject, HandlerOutcome> handler) => _dispatcher.Register(type, handler);

        public void Start() => _transport.Start();

        public void Stop() => _transport.Stop();

        public void Dispose()
        {
            _transport.MessageReceived -= OnMessage;
            _transport.Stop();
        }

        /// <summary>
        /// Dispatches all queued commands and frames each response back. Call on the
        /// editor main thread (e.g. from EditorApplication.update). Returns the
        /// number processed.
        /// </summary>
        public int Drain() => _queue.DrainAll();

        private void OnMessage(string message, Action<string> respond)
        {
            CommandRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<CommandRequest>(message);
            }
            catch (Exception ex)
            {
                // Parse failures need no main-thread work — answer immediately.
                respond(CommandResult.FromOutcome(null, HandlerOutcome.Fail($"Invalid command JSON: {ex.Message}", "PARSE_ERROR")).ToJson());
                return;
            }

            if (request == null)
            {
                respond(CommandResult.FromOutcome(null, HandlerOutcome.Fail("Empty command", "PARSE_ERROR")).ToJson());
                return;
            }

            _queue.Enqueue(request, result => respond(result.ToJson()));
        }
    }
}
