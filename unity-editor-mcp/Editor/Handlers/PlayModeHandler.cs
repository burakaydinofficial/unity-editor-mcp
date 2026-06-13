using System;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles play mode control commands (play, pause, stop, get_state)
    /// </summary>
    public static class PlayModeHandler
    {
        public static JObject HandleCommand(string command, JObject parameters)
        {
            try
            {
                switch (command)
                {
                    case "play_game":
                        return HandlePlay();
                    case "pause_game":
                        return HandlePause();
                    case "stop_game":
                        return HandleStop();
                    case "get_editor_state":
                        return HandleGetState();
                    default:
                        return CreateErrorResponse($"Unknown play mode command: {command}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlayModeHandler] Error handling command {command}: {e.Message}\n{e.StackTrace}");
                return CreateErrorResponse($"Error handling command: {e.Message}");
            }
        }

        private static JObject HandlePlay()
        {
            try
            {
                string message;
                if (!EditorApplication.isPlaying)
                {
                    EditorApplication.isPlaying = true;
                    message = "Entered play mode";
                }
                else
                {
                    message = "Already in play mode";
                }

                return CreateSuccessResponse(message, GetEditorState());
            }
            catch (Exception e)
            {
                return CreateErrorResponse($"Error entering play mode: {e.Message}");
            }
        }

        private static JObject HandlePause()
        {
            try
            {
                if (EditorApplication.isPlaying)
                {
                    EditorApplication.isPaused = !EditorApplication.isPaused;
                    string message = EditorApplication.isPaused ? "Game paused" : "Game resumed";
                    return CreateSuccessResponse(message, GetEditorState());
                }
                
                return CreateErrorResponse("Cannot pause/resume: Not in play mode");
            }
            catch (Exception e)
            {
                return CreateErrorResponse($"Error pausing/resuming game: {e.Message}");
            }
        }

        private static JObject HandleStop()
        {
            try
            {
                string message;
                if (EditorApplication.isPlaying)
                {
                    EditorApplication.isPlaying = false;
                    message = "Exited play mode";
                }
                else
                {
                    message = "Already stopped (not in play mode)";
                }

                return CreateSuccessResponse(message, GetEditorState());
            }
            catch (Exception e)
            {
                return CreateErrorResponse($"Error stopping play mode: {e.Message}");
            }
        }

        private static JObject HandleGetState()
        {
            try
            {
                var state = GetEditorState();
                // Return state as an opaque payload — no protocol envelope keys.
                // Response.Result/ResponseClassifier add the success envelope on the wire,
                // so emitting status here would nest { status:"success", state } inside the
                // result field the client receives.
                return new JObject
                {
                    ["state"] = state
                };
            }
            catch (Exception e)
            {
                return CreateErrorResponse($"Error getting editor state: {e.Message}");
            }
        }

        private static JObject GetEditorState()
        {
            return new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["applicationPath"] = EditorApplication.applicationPath,
                ["applicationContentsPath"] = EditorApplication.applicationContentsPath,
                ["timeSinceStartup"] = EditorApplication.timeSinceStartup
            };
        }

        private static JObject CreateSuccessResponse(string message, JObject state)
        {
            return new JObject
            {
                ["message"] = message,
                ["state"] = state
            };
        }

        private static JObject CreateErrorResponse(string error)
        {
            // Error as an opaque { error } payload — ResponseClassifier classifies it as a
            // real error envelope; an inline status:"error" would just be duplicated.
            return new JObject
            {
                ["error"] = error
            };
        }
    }
}