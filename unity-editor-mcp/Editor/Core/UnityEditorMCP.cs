using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEditorMCP.Models;
using UnityEditorMCP.Helpers;
using UnityEditorMCP.Logging;
using UnityEditorMCP.Handlers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// Main Unity Editor MCP class that handles TCP communication and command processing
    /// </summary>
    [InitializeOnLoad]
    public static class UnityEditorMCP
    {
        // The live transport is Core's dotnet-tested TcpTransport (ADR 0002): framing/accept/send
        // come from Core.
        private static TcpTransport _transport;
        private static readonly Queue<(Command command, Action<string> respond)> commandQueue = new Queue<(Command, Action<string>)>();
        private static readonly object queueLock = new object();

        /// <summary>Bridges Core's logging seam (IMcpLogger) to the Unity console.</summary>
        private sealed class EditorLogger : IMcpLogger
        {
            public static readonly EditorLogger Instance = new EditorLogger();
            public void Info(string message) => Debug.Log($"[Unity Editor MCP] {message}");
            public void Warn(string message) => Debug.LogWarning($"[Unity Editor MCP] {message}");
            public void Error(string message) => Debug.LogError($"[Unity Editor MCP] {message}");
        }

        // Core's dotnet-tested CommandDispatcher is the SOLE dispatch front: every command is
        // served by the tested Core path (HandlerOutcome -> CommandResult), and an unregistered
        // type yields a proper UNKNOWN_COMMAND error. The legacy ProcessCommand switch has been
        // fully retired (v0.4.0 dispatch-rail migration).
        private static readonly CommandDispatcher _dispatcher = BuildDispatcher();

        private static CommandDispatcher BuildDispatcher()
        {
            var dispatcher = new CommandDispatcher(EditorLogger.Instance);
            // Payload built lazily (per call), so no UnityEditor APIs run at static init.
            // Same wire shape as the old switch case: the Handshake serialized via its
            // own ToJson, wrapped once by CommandResult.
            dispatcher.Register("handshake", _ =>
                HandlerOutcome.Ok(JObject.Parse(BuildHandshakePayload().ToJson())));
            // First handler written natively to the HandlerOutcome contract — also
            // closes the long-standing get_component_types known gap.
            dispatcher.Register("get_component_types", ComponentHandler.GetComponentTypes);
            // PlayMode (migration batch 1) — Shape B: one registration per command, thin lambda
            // into the handler's action switch (now returning HandlerOutcome).
            dispatcher.Register("play_game", p => PlayModeHandler.HandleCommand("play_game", p));
            dispatcher.Register("pause_game", p => PlayModeHandler.HandleCommand("pause_game", p));
            dispatcher.Register("stop_game", p => PlayModeHandler.HandleCommand("stop_game", p));
            dispatcher.Register("get_editor_state", p => PlayModeHandler.HandleCommand("get_editor_state", p));
            // Editor & project ops (migration batch 2) — Shape A: single-method handlers, registered directly.
            dispatcher.Register("get_editor_info", EditorInfoHandler.GetEditorInfo);
            dispatcher.Register("get_project_settings", EditorInfoHandler.GetProjectSettings);
            dispatcher.Register("list_packages", EditorInfoHandler.ListPackages);
            dispatcher.Register("set_project_setting", EditorInfoHandler.SetProjectSetting);
            dispatcher.Register("manage_packages", EditorInfoHandler.ManagePackages);
            dispatcher.Register("quit_editor", EditorInfoHandler.QuitEditor);
            // Batch A (single-method handlers). Their now-dead legacy switch cases are removed wholesale
            // at the capstone (the rail wins via IsRegistered, so the cases are unreachable meanwhile).
            dispatcher.Register("create_gameobject", GameObjectHandler.CreateGameObject);
            dispatcher.Register("find_gameobject", GameObjectHandler.FindGameObjects);
            dispatcher.Register("modify_gameobject", GameObjectHandler.ModifyGameObject);
            dispatcher.Register("delete_gameobject", GameObjectHandler.DeleteGameObject);
            dispatcher.Register("get_hierarchy", GameObjectHandler.GetHierarchy);
            dispatcher.Register("create_scene", SceneHandler.CreateScene);
            dispatcher.Register("load_scene", SceneHandler.LoadScene);
            dispatcher.Register("save_scene", SceneHandler.SaveScene);
            dispatcher.Register("list_scenes", SceneHandler.ListScenes);
            dispatcher.Register("get_scene_info", SceneHandler.GetSceneInfo);
            dispatcher.Register("get_gameobject_details", SceneAnalysisHandler.GetGameObjectDetails);
            dispatcher.Register("analyze_scene_contents", SceneAnalysisHandler.AnalyzeSceneContents);
            dispatcher.Register("get_component_values", SceneAnalysisHandler.GetComponentValues);
            dispatcher.Register("find_by_component", SceneAnalysisHandler.FindByComponent);
            dispatcher.Register("get_object_references", SceneAnalysisHandler.GetObjectReferences);
            dispatcher.Register("get_symbols", CodeIntelligenceHandler.GetSymbols);
            dispatcher.Register("find_symbol", CodeIntelligenceHandler.FindSymbol);
            dispatcher.Register("find_references", CodeIntelligenceHandler.FindReferences);
            dispatcher.Register("get_symbol_body", CodeIntelligenceHandler.GetSymbolBody);
            dispatcher.Register("resolve_symbol", CodeIntelligenceHandler.ResolveSymbol);
            dispatcher.Register("get_type_members", CodeIntelligenceHandler.GetTypeMembers);
            dispatcher.Register("find_implementations", CodeIntelligenceHandler.FindImplementations);
            dispatcher.Register("export_roslyn_model", RoslynModelExporter.Export);
            dispatcher.Register("inspect_serialized_object", SerializedMemberHandler.Inspect);
            dispatcher.Register("set_serialized_properties", SerializedMemberHandler.Set);
            dispatcher.Register("save_assets", SerializedMemberHandler.SaveAssets);
            dispatcher.Register("find_ui_elements", UIInteractionHandler.FindUIElements);
            dispatcher.Register("click_ui_element", UIInteractionHandler.ClickUIElement);
            dispatcher.Register("get_ui_element_state", UIInteractionHandler.GetUIElementState);
            dispatcher.Register("set_ui_element_value", UIInteractionHandler.SetUIElementValue);
            dispatcher.Register("simulate_ui_input", UIInteractionHandler.SimulateUIInput);
            dispatcher.Register("add_component", ComponentHandler.AddComponent);
            dispatcher.Register("remove_component", ComponentHandler.RemoveComponent);
            dispatcher.Register("modify_component", ComponentHandler.ModifyComponent);
            dispatcher.Register("list_components", ComponentHandler.ListComponents);
            dispatcher.Register("start_compilation_monitoring", CompilationHandler.StartCompilationMonitoring);
            dispatcher.Register("stop_compilation_monitoring", CompilationHandler.StopCompilationMonitoring);
            dispatcher.Register("get_compilation_state", CompilationHandler.GetCompilationState);
            dispatcher.Register("capture_screenshot", ScreenshotHandler.CaptureScreenshot);
            dispatcher.Register("analyze_screenshot", ScreenshotHandler.AnalyzeScreenshot);
            dispatcher.Register("execute_menu_item", MenuHandler.ExecuteMenuItem);
            dispatcher.Register("clear_console", ConsoleHandler.ClearConsole);
            dispatcher.Register("enhanced_read_logs", ConsoleHandler.EnhancedReadLogs);
            // Batch B — core system commands (lifted from the legacy inline cases into SystemHandler).
            dispatcher.Register("ping", SystemHandler.Ping);
            dispatcher.Register("read_logs", SystemHandler.ReadLogs);
            dispatcher.Register("clear_logs", SystemHandler.ClearLogs);
            dispatcher.Register("refresh_assets", SystemHandler.RefreshAssets);
            // Batch B — asset / script / test single-method handlers (Shape A).
            dispatcher.Register("create_prefab", AssetManagementHandler.CreatePrefab);
            dispatcher.Register("modify_prefab", AssetManagementHandler.ModifyPrefab);
            dispatcher.Register("instantiate_prefab", AssetManagementHandler.InstantiatePrefab);
            dispatcher.Register("create_material", AssetManagementHandler.CreateMaterial);
            dispatcher.Register("modify_material", AssetManagementHandler.ModifyMaterial);
            dispatcher.Register("open_prefab", AssetManagementHandler.OpenPrefab);
            dispatcher.Register("exit_prefab_mode", AssetManagementHandler.ExitPrefabMode);
            dispatcher.Register("save_prefab", AssetManagementHandler.SavePrefab);
            dispatcher.Register("create_script", ScriptHandler.CreateScript);
            dispatcher.Register("read_script", ScriptHandler.ReadScript);
            dispatcher.Register("update_script", ScriptHandler.UpdateScript);
            dispatcher.Register("delete_script", ScriptHandler.DeleteScript);
            dispatcher.Register("list_scripts", ScriptHandler.ListScripts);
            dispatcher.Register("validate_script", ScriptHandler.ValidateScript);
            dispatcher.Register("list_tests", TestRunnerHandler.ListTests);
            dispatcher.Register("run_tests", TestRunnerHandler.RunTests);
            dispatcher.Register("get_test_results", TestRunnerHandler.GetTestResults);
            dispatcher.Register("cancel_tests", TestRunnerHandler.CancelTests);
            // Batch B — action-dispatch handlers (Shape B): one registration; HandleCommand reads params["action"].
            dispatcher.Register("manage_tags", p => TagManagementHandler.HandleCommand(p["action"]?.ToString() ?? string.Empty, p));
            dispatcher.Register("manage_layers", p => LayerManagementHandler.HandleCommand(p["action"]?.ToString() ?? string.Empty, p));
            dispatcher.Register("manage_selection", p => SelectionHandler.HandleCommand(p["action"]?.ToString() ?? string.Empty, p));
            dispatcher.Register("manage_windows", p => WindowManagementHandler.HandleCommand(p["action"]?.ToString() ?? string.Empty, p));
            dispatcher.Register("manage_tools", p => ToolManagementHandler.HandleCommand(p["action"]?.ToString() ?? string.Empty, p));
            dispatcher.Register("manage_asset_import_settings", p => AssetImportSettingsHandler.HandleCommand(p["action"]?.ToString() ?? string.Empty, p));
            dispatcher.Register("manage_asset_database", p => AssetDatabaseHandler.HandleCommand(p["action"]?.ToString() ?? string.Empty, p));
            dispatcher.Register("analyze_asset_dependencies", p => AssetDependencyHandler.HandleCommand(p["action"]?.ToString() ?? string.Empty, p));
            return dispatcher;
        }

        private static Handshake BuildHandshakePayload()
        {
            // The manifest is the public tool surface: it already excludes internal commands
            // (handshake, clear_logs) and known gaps. Advertise it as Commands AND derive the
            // names-only availableCommands from it, so the rich and the degraded (older-build) views
            // stay consistent and neither exposes an internal command. EditorCommands stays the full
            // registered surface for conformance. (ADR 0004; delta-audit finding.)
            var manifest = JArray.Parse(CommandCatalog.CommandManifestJson);
            return new Handshake
            {
                UnityVersion = Application.unityVersion,
                ProjectPath = ProjectRoot(),
                AvailableCommands = manifest.Select(c => (string)c["name"]).Where(n => n != null).ToArray(),
                Commands = manifest,
            };
        }

        // volatile: written from the background transport thread (OnMessage), read on
        // the main thread (drain/start/shutdown) — ensures cross-thread visibility.
        private static volatile McpStatus _status = McpStatus.NotConfigured;
        public static McpStatus Status
        {
            get => _status;
            private set
            {
                if (_status != value)
                {
                    _status = value;
                    Debug.Log($"[Unity Editor MCP] Status changed to: {value}");
                }
            }
        }
        
        public const int DEFAULT_PORT = 6400;
        private static int currentPort = DEFAULT_PORT;

        // Per-project deterministic port so concurrent editors don't collide on a
        // single fixed port (C3). Override with the UNITY_MCP_PORT env var.
        private static int ResolveInitialPort()
        {
            var env = Environment.GetEnvironmentVariable("UNITY_MCP_PORT");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var port) && port > 1024 && port < 65536)
            {
                return port;
            }
            return EndpointAddressing.DerivePort(ProjectRoot());
        }

        /// <summary>The project root (the folder that contains Assets/).</summary>
        private static string ProjectRoot()
        {
            var data = Application.dataPath.Replace('\\', '/');
            const string assets = "/Assets";
            return data.EndsWith(assets) ? data.Substring(0, data.Length - assets.Length) : data;
        }

        /// <summary>
        /// Static constructor - called when Unity loads
        /// </summary>
        static UnityEditorMCP()
        {
            Debug.Log("[Unity Editor MCP] Initializing...");
            currentPort = ResolveInitialPort();
            EditorApplication.update += ProcessCommandQueue;
            EditorApplication.quitting += Shutdown;
            // Release the listener/port BEFORE a domain reload (recompile) tears down
            // this domain; otherwise the orphaned TcpListener keeps the port OS-bound
            // and the next load falls back to an ephemeral port, churning the published
            // discovery port. Fires once per reload for this domain (Unity 2017.2+).
            AssemblyReloadEvents.beforeAssemblyReload += StopTcpListener;

            // Start the TCP listener
            StartTcpListener();
        }
        
        /// <summary>
        /// Starts the TCP listener on the configured port, falling back to an
        /// OS-assigned ephemeral port if it is taken. Whatever port is actually
        /// bound is published to the discovery registry (ADR 0003), so clients
        /// resolve the real endpoint from there rather than assuming a number.
        /// </summary>
        private static void StartTcpListener()
        {
            if (_transport != null)
            {
                StopTcpListener();
            }

            if (TryStartListener(currentPort)) return;

            Debug.LogWarning($"[Unity Editor MCP] Port {currentPort} is in use; falling back to an OS-assigned port (published via the discovery registry).");
            if (TryStartListener(0)) return;

            Status = McpStatus.Error;
            Debug.LogError("[Unity Editor MCP] Failed to start TCP listener on both the configured and a fallback port.");
        }

        private static bool TryStartListener(int port)
        {
            var transport = new TcpTransport(IPAddress.Loopback, port, EditorLogger.Instance);
            try
            {
                transport.MessageReceived += OnMessage;
                transport.Start(); // throws SocketException if the port is taken

                _transport = transport;
                currentPort = transport.Port;

                Status = McpStatus.Disconnected;
                Debug.Log($"[Unity Editor MCP] TCP listener started on port {currentPort}");
                PublishDescriptor();
                return true;
            }
            catch (SocketException ex)
            {
                Debug.LogWarning($"[Unity Editor MCP] Could not bind port {port}: {ex.Message}");
                transport.MessageReceived -= OnMessage;
                try { transport.Stop(); } catch { /* ignore */ }
                return false;
            }
            catch (Exception ex)
            {
                Status = McpStatus.Error;
                Debug.LogError($"[Unity Editor MCP] Unexpected error starting TCP listener: {ex}");
                transport.MessageReceived -= OnMessage;
                try { transport.Stop(); } catch { /* ignore */ }
                return false;
            }
        }

        private static InstanceRegistry instanceRegistry;
        private static DateTime startedAtUtc = DateTime.UtcNow;
        private static DateTime lastHeartbeatUtc;
        private const double HeartbeatIntervalSeconds = 60;

        /// <summary>Publishes (or refreshes) this editor's discovery descriptor.</summary>
        private static void PublishDescriptor()
        {
            try
            {
                if (instanceRegistry == null)
                {
                    instanceRegistry = new InstanceRegistry(InstanceRegistry.DefaultDirectory());
                }
                var now = DateTime.UtcNow;
                instanceRegistry.Publish(new InstanceDescriptor
                {
                    ProjectPath = ProjectRoot(),
                    Port = currentPort,
                    Pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                    Host = Environment.MachineName,
                    UnityVersion = Application.unityVersion,
                    StartedAtUtc = startedAtUtc,
                    LastHeartbeatUtc = now,
                });
                lastHeartbeatUtc = now;
                // Opportunistically clear crashed/stale peers (cheap, main thread).
                instanceRegistry.ReapStale(now, Environment.MachineName);
            }
            catch (Exception ex)
            {
                // Discovery is best-effort; the bridge still works via explicit port config.
                Debug.LogWarning($"[Unity Editor MCP] Could not publish discovery descriptor: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Stops the TCP listener
        /// </summary>
        private static void StopTcpListener()
        {
            try
            {
                if (_transport != null)
                {
                    _transport.MessageReceived -= OnMessage;
                    _transport.Stop();
                    _transport = null;
                }

                Status = McpStatus.Disconnected;
                Debug.Log("[Unity Editor MCP] TCP listener stopped");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Unity Editor MCP] Error stopping TCP listener: {ex}");
            }
        }
        
        /// <summary>
        /// Called by TcpTransport (on a background thread) for each complete framed
        /// message. Mirrors the old per-message logic: the raw "ping" probe is
        /// answered inline; otherwise the command is parsed and queued for the main
        /// thread. `respond` frames and sends the reply to the originating client.
        /// </summary>
        private static void OnMessage(string json, Action<string> respond)
        {
            Status = McpStatus.Connected;
            try
            {
                if (json.Trim().ToLower() == "ping")
                {
                    respond(Response.Pong());
                    return;
                }

                var command = JsonConvert.DeserializeObject<Command>(json);
                if (command == null)
                {
                    respond(Response.ErrorResult("Invalid command format", "PARSE_ERROR", null));
                    return;
                }

                lock (queueLock)
                {
                    commandQueue.Enqueue((command, respond));
                }
            }
            catch (JsonException ex)
            {
                respond(Response.ErrorResult($"JSON parsing error: {ex.Message}", "JSON_ERROR", null));
            }
        }
        
        /// <summary>
        /// Processes queued commands on the Unity main thread
        /// </summary>
        private static void ProcessCommandQueue()
        {
            // Keep the discovery descriptor fresh while the listener is alive
            // (stale descriptors are reaped after 300s; see InstanceRegistry).
            if (_transport != null && (DateTime.UtcNow - lastHeartbeatUtc).TotalSeconds >= HeartbeatIntervalSeconds)
            {
                PublishDescriptor();
            }

            // Drain the queue under the lock, then dispatch OUTSIDE it so a slow handler
            // (or a blocked respond) never stalls the background network thread that is
            // trying to enqueue newly-arrived messages.
            List<(Command command, Action<string> respond)> batch;
            lock (queueLock)
            {
                if (commandQueue.Count == 0) return;
                batch = new List<(Command, Action<string>)>(commandQueue.Count);
                while (commandQueue.Count > 0) batch.Add(commandQueue.Dequeue());
            }

            foreach (var (command, respond) in batch)
            {
                // Every command rides the Core CommandDispatcher rail; Dispatch returns a proper
                // UNKNOWN_COMMAND error for any unregistered type (the legacy switch is retired).
                DispatchViaCore(command, respond);
            }
        }

        /// <summary>
        /// Dispatches a command through Core's tested CommandDispatcher (the migrated
        /// path). Dispatch() itself never throws (handler errors become error results),
        /// but the surrounding ToJson()/respond() can — so the catch sends a best-effort
        /// error reply rather than leaving the client to time out.
        /// </summary>
        private static void DispatchViaCore(Command command, Action<string> respond)
        {
            try
            {
                var request = new CommandRequest
                {
                    Id = command?.Id,
                    Type = command?.Type,
                    Params = command?.Parameters
                };
                respond(_dispatcher.Dispatch(request).ToJson());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Unity Editor MCP] Error dispatching {command?.Type}: {ex}");
                // Dispatch() never throws, but ToJson() (non-serializable payload) or
                // respond() (client gone) can. Best-effort error reply so the Node
                // client gets a response instead of waiting out the 30s timeout.
                try
                {
                    respond(Response.ErrorResult(command?.Id, $"Internal error: {ex.Message}", "INTERNAL_ERROR"));
                }
                catch (Exception sendEx)
                {
                    Debug.LogError($"[Unity Editor MCP] Failed to send error reply for {command?.Type}: {sendEx.Message}");
                }
            }
        }
        
        
        /// <summary>
        /// Shuts down the MCP system
        /// </summary>
        private static void Shutdown()
        {
            Debug.Log("[Unity Editor MCP] Shutting down...");
            StopTcpListener();
            try { instanceRegistry?.Remove(ProjectRoot()); } catch { /* best effort */ }
            EditorApplication.update -= ProcessCommandQueue;
            EditorApplication.quitting -= Shutdown;
            // Pair the static-ctor subscription so quit doesn't leave a dangling handler
            // that re-fires StopTcpListener on the next reload after teardown.
            AssemblyReloadEvents.beforeAssemblyReload -= StopTcpListener;
        }
        
        /// <summary>
        /// Restarts the TCP listener
        /// </summary>
        public static void Restart()
        {
            Debug.Log("[Unity Editor MCP] Restarting...");
            StopTcpListener();
            StartTcpListener();
        }
        
        /// <summary>
        /// Changes the listening port and restarts
        /// </summary>
        public static void ChangePort(int newPort)
        {
            if (newPort < 1024 || newPort > 65535)
            {
                Debug.LogError($"[Unity Editor MCP] Invalid port number: {newPort}. Must be between 1024 and 65535.");
                return;
            }
            
            currentPort = newPort;
            Restart();
        }
    }
}