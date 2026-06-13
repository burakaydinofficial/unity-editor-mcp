using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// Unity-independent TCP transport: listens on a (loopback) port, frames
    /// messages with <see cref="MessageFramer"/>, and raises
    /// <see cref="MessageReceived"/> for each complete message together with a
    /// responder that frames and sends a reply to the originating client. It knows
    /// nothing about commands — the host wires the queue/dispatcher on top. Built
    /// on <c>System.Net.Sockets</c> (no UnityEditor), so it is exercised
    /// end-to-end with a real loopback socket in <c>dotnet test</c>.
    /// </summary>
    public sealed class TcpTransport : IDisposable
    {
        private readonly IPAddress _bindAddress;
        private readonly int _requestedPort;
        private readonly IMcpLogger _log;
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        public TcpTransport(IPAddress bindAddress, int port, IMcpLogger log = null)
        {
            _bindAddress = bindAddress ?? IPAddress.Loopback;
            _requestedPort = port;
            _log = log ?? NullMcpLogger.Instance;
        }

        /// <summary>The port actually bound (resolves an ephemeral port-0 request after Start).</summary>
        public int Port { get; private set; }

        /// <summary>True while the listener is accepting connections.</summary>
        public bool IsListening { get; private set; }

        /// <summary>
        /// Raised on a background thread for each complete framed message. The
        /// second argument frames and sends a reply to the originating client; it
        /// may be invoked later (e.g. after the command is processed on the main
        /// thread) and will throw if the client has since disconnected — callers
        /// should treat a throwing responder as a dropped client.
        /// </summary>
        public event Action<string, Action<string>> MessageReceived;

        public void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            var listener = new TcpListener(_bindAddress, _requestedPort);
            listener.Start();
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            IsListening = true;
            _log.Info($"TcpTransport listening on {_bindAddress}:{Port}");
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener?.Stop(); } catch { /* ignore */ }
            IsListening = false;
            _listener = null;
            _cts = null;
        }

        public void Dispose() => Stop();

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException sx)
                {
                    // Transient per-accept failure (e.g. a client RST before the accept
                    // completes — Windows surfaces WSAECONNRESET here). The listener is
                    // still valid, so keep accepting rather than going permanently deaf.
                    if (ct.IsCancellationRequested) break;
                    _log.Warn($"Accept transient error (continuing): {sx.SocketErrorCode}");
                    try { await Task.Delay(50, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested) _log.Error($"Accept loop fatal: {ex.Message}");
                    break;
                }
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            var framer = new MessageFramer();
            var writeLock = new object();
            var buffer = new byte[4096];
            try
            {
                using (client)
                {
                    var stream = client.GetStream();

                    void Respond(string reply)
                    {
                        var framed = MessageFramer.Encode(reply);
                        lock (writeLock)
                        {
                            stream.Write(framed, 0, framed.Length);
                            stream.Flush();
                        }
                    }

                    while (!ct.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        if (read == 0) break; // client closed
                        framer.Append(buffer, 0, read);

                        try
                        {
                            while (framer.TryReadMessage(out var message))
                            {
                                MessageReceived?.Invoke(message, Respond);
                            }
                        }
                        catch (FramingException fe)
                        {
                            _log.Error($"Framing error, closing client: {fe.Message}");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.Warn($"Client handler ended: {ex.Message}");
            }
        }
    }
}
