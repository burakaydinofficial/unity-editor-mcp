using System;
using System.Collections.Generic;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// Thread-safe hand-off between the transport (which receives commands on a
    /// background thread) and the editor's main thread (which must run handlers,
    /// since UnityEditor APIs are not thread-safe). The transport calls
    /// <see cref="Enqueue"/>; the Unity layer calls <see cref="DrainAll"/> from
    /// <c>EditorApplication.update</c>. Unity-independent and unit-tested.
    /// </summary>
    public sealed class CommandQueue
    {
        private readonly Queue<Item> _queue = new Queue<Item>();
        private readonly object _lock = new object();
        private readonly CommandDispatcher _dispatcher;
        private readonly IMcpLogger _log;

        public CommandQueue(CommandDispatcher dispatcher, IMcpLogger log = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _log = log ?? NullMcpLogger.Instance;
        }

        /// <summary>Number of commands awaiting drain.</summary>
        public int Count
        {
            get { lock (_lock) { return _queue.Count; } }
        }

        /// <summary>Queues a command together with the callback that delivers its response.</summary>
        public void Enqueue(CommandRequest request, Action<CommandResult> respond)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (respond == null) throw new ArgumentNullException(nameof(respond));
            lock (_lock) { _queue.Enqueue(new Item(request, respond)); }
        }

        /// <summary>
        /// Dispatches every currently-queued command in FIFO order and invokes
        /// each responder. Must be called on the main thread. A responder that
        /// throws is logged and does not stop the drain. Returns the number
        /// processed.
        /// </summary>
        public int DrainAll()
        {
            int processed = 0;
            while (true)
            {
                Item item;
                lock (_lock)
                {
                    if (_queue.Count == 0) break;
                    item = _queue.Dequeue();
                }

                CommandResult result = _dispatcher.Dispatch(item.Request);
                try
                {
                    item.Respond(result);
                }
                catch (Exception ex)
                {
                    _log.Error($"Response delivery failed for '{item.Request.Type}': {ex.Message}");
                }
                processed++;
            }
            return processed;
        }

        /// <summary>Discards all queued commands without dispatching them.</summary>
        public void Clear()
        {
            lock (_lock) { _queue.Clear(); }
        }

        private readonly struct Item
        {
            public readonly CommandRequest Request;
            public readonly Action<CommandResult> Respond;

            public Item(CommandRequest request, Action<CommandResult> respond)
            {
                Request = request;
                Respond = respond;
            }
        }
    }
}
