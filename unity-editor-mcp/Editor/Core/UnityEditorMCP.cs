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
        // The live transport is now Core's dotnet-tested TcpTransport (ADR 0002).
        // Slice 1 of the McpBridge migration: framing/accept/send come from Core;
        // the legacy ProcessCommand switch stays as the message processor for now.
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

        // Slice 2: Core's dotnet-tested CommandDispatcher is the live dispatch front.
        // Commands registered here are served by the tested Core path (HandlerOutcome
        // -> CommandResult); everything else falls through to the legacy ProcessCommand
        // switch. Handlers migrate onto this rail one at a time (strangler).
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
            return dispatcher;
        }

        private static Handshake BuildHandshakePayload() => new Handshake
        {
            UnityVersion = Application.unityVersion,
            ProjectPath = ProjectRoot(),
            AvailableCommands = CommandCatalog.EditorCommands
                .Except(CommandCatalog.KnownEditorGaps)
                .ToArray(),
        };

        private static McpStatus _status = McpStatus.NotConfigured;
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

            lock (queueLock)
            {
                while (commandQueue.Count > 0)
                {
                    var (command, respond) = commandQueue.Dequeue();
                    if (command?.Type != null && _dispatcher.IsRegistered(command.Type))
                    {
                        DispatchViaCore(command, respond);
                    }
                    else
                    {
                        ProcessCommand(command, respond);
                    }
                }
            }
        }

        /// <summary>
        /// Dispatches a command through Core's tested CommandDispatcher (the migrated
        /// path). The dispatcher never throws — it turns handler errors into a proper
        /// error result — so the only guarded failure here is the client vanishing
        /// mid-send.
        /// </summary>
        private static void DispatchViaCore(Command command, Action<string> respond)
        {
            try
            {
                var request = new CommandRequest
                {
                    Id = command.Id,
                    Type = command.Type,
                    Params = command.Parameters
                };
                respond(_dispatcher.Dispatch(request).ToJson());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Unity Editor MCP] Error dispatching {command?.Type}: {ex}");
            }
        }
        
        /// <summary>
        /// Processes a single command
        /// </summary>
        private static void ProcessCommand(Command command, Action<string> respond)
        {
            try
            {
                Debug.Log($"[Unity Editor MCP] Processing command: {JsonConvert.SerializeObject(command)}");
                
                string response;
                
                // Handle command based on type
                switch (command.Type?.ToLower())
                {
                    case "ping":
                        var pongData = new
                        {
                            message = "pong",
                            echo = command.Parameters?["message"]?.ToString(),
                            timestamp = System.DateTime.UtcNow.ToString("o")
                        };
                        // Use new format with command ID
                        response = Response.Result(command.Id, pongData);
                        break;
                        
                    case "read_logs":
                        // Parse parameters
                        int count = 100;
                        string logTypeFilter = null;
                        
                        if (command.Parameters != null)
                        {
                            if (command.Parameters.ContainsKey("count"))
                            {
                                if (int.TryParse(command.Parameters["count"].ToString(), out int parsedCount))
                                {
                                    count = Math.Min(Math.Max(parsedCount, 1), 1000); // Clamp between 1 and 1000
                                }
                            }
                            
                            if (command.Parameters.ContainsKey("logType"))
                            {
                                logTypeFilter = command.Parameters["logType"].ToString();
                            }
                        }
                        
                        // Get logs
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
                        
                        response = Response.Result(command.Id, new
                        {
                            logs = logData,
                            count = logData.Count,
                            totalCaptured = logs.Count
                        });
                        break;
                        
                    case "clear_logs":
                        LogCapture.ClearLogs();
                        response = Response.Result(command.Id, new
                        {
                            message = "Logs cleared successfully",
                            timestamp = System.DateTime.UtcNow.ToString("o")
                        });
                        break;
                        
                    case "refresh_assets":
                        // Trigger Unity to recompile and refresh assets
                        AssetDatabase.Refresh();
                        
                        // Check if Unity is compiling
                        bool isCompiling = EditorApplication.isCompiling;
                        
                        response = Response.Result(command.Id, new
                        {
                            message = "Asset refresh triggered",
                            isCompiling = isCompiling,
                            timestamp = System.DateTime.UtcNow.ToString("o")
                        });
                        break;
                        
                    case "create_gameobject":
                        var createResult = GameObjectHandler.CreateGameObject(command.Parameters);
                        response = Response.Result(command.Id, createResult);
                        break;
                        
                    case "find_gameobject":
                        var findResult = GameObjectHandler.FindGameObjects(command.Parameters);
                        response = Response.Result(command.Id, findResult);
                        break;
                        
                    case "modify_gameobject":
                        var modifyResult = GameObjectHandler.ModifyGameObject(command.Parameters);
                        response = Response.Result(command.Id, modifyResult);
                        break;
                        
                    case "delete_gameobject":
                        var deleteResult = GameObjectHandler.DeleteGameObject(command.Parameters);
                        response = Response.Result(command.Id, deleteResult);
                        break;
                        
                    case "get_hierarchy":
                        var hierarchyResult = GameObjectHandler.GetHierarchy(command.Parameters);
                        response = Response.Result(command.Id, hierarchyResult);
                        break;
                        
                    case "create_scene":
                        var createSceneResult = SceneHandler.CreateScene(command.Parameters);
                        response = Response.Result(command.Id, createSceneResult);
                        break;
                        
                    case "load_scene":
                        var loadSceneResult = SceneHandler.LoadScene(command.Parameters);
                        response = Response.Result(command.Id, loadSceneResult);
                        break;
                        
                    case "save_scene":
                        var saveSceneResult = SceneHandler.SaveScene(command.Parameters);
                        response = Response.Result(command.Id, saveSceneResult);
                        break;
                        
                    case "list_scenes":
                        var listScenesResult = SceneHandler.ListScenes(command.Parameters);
                        response = Response.Result(command.Id, listScenesResult);
                        break;
                        
                    case "get_scene_info":
                        var getSceneInfoResult = SceneHandler.GetSceneInfo(command.Parameters);
                        response = Response.Result(command.Id, getSceneInfoResult);
                        break;
                        
                    case "get_gameobject_details":
                        var getGameObjectDetailsResult = SceneAnalysisHandler.GetGameObjectDetails(command.Parameters);
                        response = Response.Result(command.Id, getGameObjectDetailsResult);
                        break;
                        
                    case "analyze_scene_contents":
                        var analyzeSceneResult = SceneAnalysisHandler.AnalyzeSceneContents(command.Parameters);
                        response = Response.Result(command.Id, analyzeSceneResult);
                        break;
                        
                    case "get_component_values":
                        var getComponentValuesResult = SceneAnalysisHandler.GetComponentValues(command.Parameters);
                        response = Response.Result(command.Id, getComponentValuesResult);
                        break;
                        
                    case "find_by_component":
                        var findByComponentResult = SceneAnalysisHandler.FindByComponent(command.Parameters);
                        response = Response.Result(command.Id, findByComponentResult);
                        break;
                        
                    case "get_object_references":
                        var getObjectReferencesResult = SceneAnalysisHandler.GetObjectReferences(command.Parameters);
                        response = Response.Result(command.Id, getObjectReferencesResult);
                        break;
                        
                    // Play Mode Control commands
                    case "play_game":
                        var playResult = PlayModeHandler.HandleCommand("play_game", command.Parameters);
                        response = Response.Result(command.Id, playResult);
                        break;
                        
                    case "pause_game":
                        var pauseResult = PlayModeHandler.HandleCommand("pause_game", command.Parameters);
                        response = Response.Result(command.Id, pauseResult);
                        break;
                        
                    case "stop_game":
                        var stopResult = PlayModeHandler.HandleCommand("stop_game", command.Parameters);
                        response = Response.Result(command.Id, stopResult);
                        break;
                        
                    case "get_editor_state":
                        var stateResult = PlayModeHandler.HandleCommand("get_editor_state", command.Parameters);
                        response = Response.Result(command.Id, stateResult);
                        break;
                        
                    // UI Interaction commands
                    case "find_ui_elements":
                        var findUIResult = UIInteractionHandler.FindUIElements(command.Parameters);
                        response = Response.Result(command.Id, findUIResult);
                        break;
                        
                    case "click_ui_element":
                        var clickUIResult = UIInteractionHandler.ClickUIElement(command.Parameters);
                        response = Response.Result(command.Id, clickUIResult);
                        break;
                        
                    case "get_ui_element_state":
                        var getUIStateResult = UIInteractionHandler.GetUIElementState(command.Parameters);
                        response = Response.Result(command.Id, getUIStateResult);
                        break;
                        
                    case "set_ui_element_value":
                        var setUIValueResult = UIInteractionHandler.SetUIElementValue(command.Parameters);
                        response = Response.Result(command.Id, setUIValueResult);
                        break;
                        
                    case "simulate_ui_input":
                        var simulateUIResult = UIInteractionHandler.SimulateUIInput(command.Parameters);
                        response = Response.Result(command.Id, simulateUIResult);
                        break;
                        
                    // Asset Management commands
                    case "create_prefab":
                        var createPrefabResult = AssetManagementHandler.CreatePrefab(command.Parameters);
                        response = Response.Result(command.Id, createPrefabResult);
                        break;
                        
                    case "modify_prefab":
                        var modifyPrefabResult = AssetManagementHandler.ModifyPrefab(command.Parameters);
                        response = Response.Result(command.Id, modifyPrefabResult);
                        break;
                        
                    case "instantiate_prefab":
                        var instantiatePrefabResult = AssetManagementHandler.InstantiatePrefab(command.Parameters);
                        response = Response.Result(command.Id, instantiatePrefabResult);
                        break;
                        
                    case "create_material":
                        var createMaterialResult = AssetManagementHandler.CreateMaterial(command.Parameters);
                        response = Response.Result(command.Id, createMaterialResult);
                        break;
                        
                    case "modify_material":
                        var modifyMaterialResult = AssetManagementHandler.ModifyMaterial(command.Parameters);
                        response = Response.Result(command.Id, modifyMaterialResult);
                        break;
                        
                    case "open_prefab":
                        var openPrefabResult = AssetManagementHandler.OpenPrefab(command.Parameters);
                        response = Response.Result(command.Id, openPrefabResult);
                        break;
                        
                    case "exit_prefab_mode":
                        var exitPrefabModeResult = AssetManagementHandler.ExitPrefabMode(command.Parameters);
                        response = Response.Result(command.Id, exitPrefabModeResult);
                        break;
                        
                    case "save_prefab":
                        var savePrefabResult = AssetManagementHandler.SavePrefab(command.Parameters);
                        response = Response.Result(command.Id, savePrefabResult);
                        break;
                        
                    // Script Management commands
                    case "create_script":
                        var createScriptResult = ScriptHandler.CreateScript(command.Parameters);
                        response = Response.Result(command.Id, createScriptResult);
                        break;
                        
                    case "read_script":
                        var readScriptResult = ScriptHandler.ReadScript(command.Parameters);
                        response = Response.Result(command.Id, readScriptResult);
                        break;
                        
                    case "update_script":
                        var updateScriptResult = ScriptHandler.UpdateScript(command.Parameters);
                        response = Response.Result(command.Id, updateScriptResult);
                        break;
                        
                    case "delete_script":
                        var deleteScriptResult = ScriptHandler.DeleteScript(command.Parameters);
                        response = Response.Result(command.Id, deleteScriptResult);
                        break;
                        
                    case "list_scripts":
                        var listScriptsResult = ScriptHandler.ListScripts(command.Parameters);
                        response = Response.Result(command.Id, listScriptsResult);
                        break;
                        
                    case "validate_script":
                        var validateScriptResult = ScriptHandler.ValidateScript(command.Parameters);
                        response = Response.Result(command.Id, validateScriptResult);
                        break;
                        
                    case "execute_menu_item":
                        var executeMenuResult = MenuHandler.ExecuteMenuItem(command.Parameters);
                        response = Response.Result(command.Id, executeMenuResult);
                        break;
                        
                    case "clear_console":
                        var clearConsoleResult = ConsoleHandler.ClearConsole(command.Parameters);
                        response = Response.Result(command.Id, clearConsoleResult);
                        break;
                        
                    case "enhanced_read_logs":
                        var enhancedReadLogsResult = ConsoleHandler.EnhancedReadLogs(command.Parameters);
                        response = Response.Result(command.Id, enhancedReadLogsResult);
                        break;
                        
                    // Screenshot commands
                    case "capture_screenshot":
                        var captureScreenshotResult = ScreenshotHandler.CaptureScreenshot(command.Parameters);
                        response = Response.Result(command.Id, captureScreenshotResult);
                        break;
                        
                    case "analyze_screenshot":
                        var analyzeScreenshotResult = ScreenshotHandler.AnalyzeScreenshot(command.Parameters);
                        response = Response.Result(command.Id, analyzeScreenshotResult);
                        break;
                        
                    // Component commands
                    case "add_component":
                        var addComponentResult = ComponentHandler.AddComponent(command.Parameters);
                        response = Response.Result(command.Id, addComponentResult);
                        break;
                        
                    case "remove_component":
                        var removeComponentResult = ComponentHandler.RemoveComponent(command.Parameters);
                        response = Response.Result(command.Id, removeComponentResult);
                        break;
                        
                    case "modify_component":
                        var modifyComponentResult = ComponentHandler.ModifyComponent(command.Parameters);
                        response = Response.Result(command.Id, modifyComponentResult);
                        break;
                        
                    case "list_components":
                        var listComponentsResult = ComponentHandler.ListComponents(command.Parameters);
                        response = Response.Result(command.Id, listComponentsResult);
                        break;
                        
                    // Compilation monitoring commands
                    case "start_compilation_monitoring":
                        var startMonitoringResult = CompilationHandler.StartCompilationMonitoring(command.Parameters);
                        response = Response.Result(command.Id, startMonitoringResult);
                        break;
                        
                    case "stop_compilation_monitoring":
                        var stopMonitoringResult = CompilationHandler.StopCompilationMonitoring(command.Parameters);
                        response = Response.Result(command.Id, stopMonitoringResult);
                        break;
                        
                    case "get_compilation_state":
                        var compilationStateResult = CompilationHandler.GetCompilationState(command.Parameters);
                        response = Response.Result(command.Id, compilationStateResult);
                        break;
                        
                    // Tag management commands
                    case "manage_tags":
                        var tagManagementResult = TagManagementHandler.HandleCommand(command.Parameters["action"]?.ToString(), command.Parameters);
                        response = Response.Result(command.Id, tagManagementResult);
                        break;
                        
                    // Layer management commands
                    case "manage_layers":
                        var layerManagementResult = LayerManagementHandler.HandleCommand(command.Parameters["action"]?.ToString(), command.Parameters);
                        response = Response.Result(command.Id, layerManagementResult);
                        break;
                        
                    // Selection management commands
                    case "manage_selection":
                        var selectionManagementResult = SelectionHandler.HandleCommand(command.Parameters["action"]?.ToString(), command.Parameters);
                        response = Response.Result(command.Id, selectionManagementResult);
                        break;
                        
                    // Window management commands
                    case "manage_windows":
                        var windowManagementResult = WindowManagementHandler.HandleCommand(command.Parameters["action"]?.ToString(), command.Parameters);
                        response = Response.Result(command.Id, windowManagementResult);
                        break;
                        
                    // Tool management commands
                    case "manage_tools":
                        var toolManagementResult = ToolManagementHandler.HandleCommand(command.Parameters["action"]?.ToString(), command.Parameters);
                        response = Response.Result(command.Id, toolManagementResult);
                        break;
                        
                    // Asset import settings commands
                    case "manage_asset_import_settings":
                        var assetImportSettingsResult = AssetImportSettingsHandler.HandleCommand(command.Parameters["action"]?.ToString(), command.Parameters);
                        response = Response.Result(command.Id, assetImportSettingsResult);
                        break;
                        
                    // Asset database commands
                    case "manage_asset_database":
                        var assetDatabaseResult = AssetDatabaseHandler.HandleCommand(command.Parameters["action"]?.ToString(), command.Parameters);
                        response = Response.Result(command.Id, assetDatabaseResult);
                        break;
                        
                    // Asset dependency analysis commands
                    case "analyze_asset_dependencies":
                        var assetDependencyResult = AssetDependencyHandler.HandleCommand(command.Parameters["action"]?.ToString(), command.Parameters);
                        response = Response.Result(command.Id, assetDependencyResult);
                        break;
                        
                    // Test Runner commands
                    case "list_tests":
                        var listTestsResult = TestRunnerHandler.ListTests(command.Parameters);
                        response = Response.Result(command.Id, listTestsResult);
                        break;
                        
                    case "run_tests":
                        var runTestsResult = TestRunnerHandler.RunTests(command.Parameters);
                        response = Response.Result(command.Id, runTestsResult);
                        break;
                        
                    case "get_test_results":
                        var getTestResultsResult = TestRunnerHandler.GetTestResults(command.Parameters);
                        response = Response.Result(command.Id, getTestResultsResult);
                        break;
                        
                    case "cancel_tests":
                        var cancelTestsResult = TestRunnerHandler.CancelTests(command.Parameters);
                        response = Response.Result(command.Id, cancelTestsResult);
                        break;

                    // NOTE: "handshake" is now served by the Core CommandDispatcher
                    // (see BuildDispatcher); ProcessCommandQueue routes it there before
                    // reaching this switch, so it intentionally has no case here.

                    default:
                        // Use new format with error details
                        response = Response.ErrorResult(
                            command.Id,
                            $"Unknown command type: {command.Type}", 
                            "UNKNOWN_COMMAND",
                            new { commandType = command.Type }
                        );
                        break;
                }
                
                // Send response via the transport's responder (frames + writes).
                respond(response);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Unity Editor MCP] Error processing command {command}: {ex}");

                try
                {
                    respond(Response.ErrorResult(
                        command.Id,
                        $"Internal error: {ex.Message}",
                        "INTERNAL_ERROR",
                        new
                        {
                            commandType = command.Type,
                            stackTrace = ex.StackTrace
                        }
                    ));
                }
                catch
                {
                    // Best effort — the client may have disconnected.
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