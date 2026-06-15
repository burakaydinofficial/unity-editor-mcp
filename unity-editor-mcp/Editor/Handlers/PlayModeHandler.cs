using System;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles play mode control commands (play, pause, stop, get_state). On the Core
    /// CommandDispatcher rail: each method returns a HandlerOutcome (Ok/Fail), so an error can
    /// never serialize as a success. Wire shape is unchanged from the legacy switch (success →
    /// { message, state } or { state }; error → a real error envelope).
    /// </summary>
    public static class PlayModeHandler
    {
        public static HandlerOutcome HandleCommand(string command, JObject parameters)
        {
            try
            {
                switch (command)
                {
                    case "play_game": return HandlePlay();
                    case "pause_game": return HandlePause();
                    case "stop_game": return HandleStop();
                    case "get_editor_state": return HandleGetState();
                    default: return HandlerOutcome.Fail($"Unknown play mode command: {command}", "VALIDATION_ERROR");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlayModeHandler] Error handling command {command}: {e.Message}\n{e.StackTrace}");
                return HandlerOutcome.Fail($"Error handling command: {e.Message}", "INTERNAL_ERROR");
            }
        }

        private static HandlerOutcome HandlePlay()
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
                return Success(message);
            }
            catch (Exception e)
            {
                return HandlerOutcome.Fail($"Error entering play mode: {e.Message}", "INTERNAL_ERROR");
            }
        }

        private static HandlerOutcome HandlePause()
        {
            try
            {
                if (EditorApplication.isPlaying)
                {
                    EditorApplication.isPaused = !EditorApplication.isPaused;
                    return Success(EditorApplication.isPaused ? "Game paused" : "Game resumed");
                }
                return HandlerOutcome.Fail("Cannot pause/resume: Not in play mode", "INVALID_STATE");
            }
            catch (Exception e)
            {
                return HandlerOutcome.Fail($"Error pausing/resuming game: {e.Message}", "INTERNAL_ERROR");
            }
        }

        private static HandlerOutcome HandleStop()
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
                return Success(message);
            }
            catch (Exception e)
            {
                return HandlerOutcome.Fail($"Error stopping play mode: {e.Message}", "INTERNAL_ERROR");
            }
        }

        private static HandlerOutcome HandleGetState()
        {
            try
            {
                return HandlerOutcome.Ok(new JObject { ["state"] = GetEditorState() });
            }
            catch (Exception e)
            {
                return HandlerOutcome.Fail($"Error getting editor state: {e.Message}", "INTERNAL_ERROR");
            }
        }

        private static HandlerOutcome Success(string message) =>
            HandlerOutcome.Ok(new JObject { ["message"] = message, ["state"] = GetEditorState() });

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
    }
}
