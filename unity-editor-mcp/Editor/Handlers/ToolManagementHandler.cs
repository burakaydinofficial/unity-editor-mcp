using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles Unity Editor tool and plugin management operations
    /// </summary>
    public static class ToolManagementHandler
    {
        private static ListRequest listRequest;
        private static Dictionary<string, ToolInfo> toolCache = new Dictionary<string, ToolInfo>();
        private static DateTime lastCacheUpdate = DateTime.MinValue;
        private static readonly TimeSpan cacheExpiry = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Handle tool management operations (get, activate, deactivate, refresh)
        /// </summary>
        public static HandlerOutcome HandleCommand(string action, JObject parameters)
        {
            try
            {
                switch (action.ToLower())
                {
                    case "get":
                        string category = parameters["category"]?.ToString();
                        return GetTools(category);
                    case "activate":
                        var toolNameToActivate = parameters["toolName"]?.ToString();
                        return ActivateTool(toolNameToActivate);
                    case "deactivate":
                        var toolNameToDeactivate = parameters["toolName"]?.ToString();
                        return DeactivateTool(toolNameToDeactivate);
                    case "refresh":
                        return RefreshToolCache();
                    default:
                        return HandlerOutcome.Fail($"Unknown action: {action}", "VALIDATION_ERROR");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ToolManagementHandler] Error handling {action}: {e.Message}");
                return HandlerOutcome.Fail(e.Message);
            }
        }

        /// <summary>
        /// Get all available tools, optionally filtered by category
        /// </summary>
        private static HandlerOutcome GetTools(string category)
        {
            try
            {
                // Ensure cache is up to date. (review round-2: the cache holds only PM-queried entries — currently
                // none; the fabricated cache was removed in round-6 #6. Gate on the expiry only — NOT on Count==0,
                // which is permanently true and would rebuild every call.)
                if (DateTime.Now - lastCacheUpdate > cacheExpiry)
                {
                    UpdateToolCache();
                }

                var tools = new List<object>();
                int installedCount = 0;
                int activeCount = 0;

                // Get built-in Unity tools
                AddBuiltInTools(tools, ref installedCount, ref activeCount, category);

                // Get package manager packages
                AddPackageManagerTools(tools, ref installedCount, ref activeCount, category);

                // Sort tools by name
                tools = tools.OrderBy(t => (t as dynamic).name).ToList();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "get",
                    tools = tools,
                    installedCount = installedCount,
                    activeCount = activeCount
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[ToolManagementHandler] Error getting tools: {e.Message}");
                return HandlerOutcome.Fail($"Failed to get tools: {e.Message}");
            }
        }

        /// <summary>
        /// Add built-in Unity tools to the list (with REAL versions, not hardcoded guesses). round-6 #6.
        /// </summary>
        private static void AddBuiltInTools(List<object> tools, ref int installedCount, ref int activeCount, string category)
        {
            AddBuiltInTool(tools, ref installedCount, ref activeCount, category, "ProBuilder", "ProBuilder", "UnityEngine.ProBuilder.ProBuilderMesh, Unity.ProBuilder", "Modeling");
            AddBuiltInTool(tools, ref installedCount, ref activeCount, category, "Cinemachine", "Cinemachine", "Cinemachine.CinemachineVirtualCamera, Cinemachine", "Camera");
            AddBuiltInTool(tools, ref installedCount, ref activeCount, category, "TextMeshPro", "TextMesh Pro", "TMPro.TextMeshProUGUI, Unity.TextMeshPro", "UI");
        }

        private static void AddBuiltInTool(List<object> tools, ref int installedCount, ref int activeCount,
            string filterCategory, string name, string displayName, string typeName, string toolCategory)
        {
            if (!string.IsNullOrEmpty(filterCategory) && filterCategory != toolCategory) return;
            var type = System.Type.GetType(typeName);
            bool installed = type != null;
            bool active = installed && IsToolWindowOpen(name);
            tools.Add(new
            {
                name = name,
                displayName = displayName,
                version = installed ? (PackageVersionForType(type) ?? "unknown") : "Not installed",
                category = toolCategory,
                isInstalled = installed,
                isActive = active
            });
            if (installed) { installedCount++; if (active) activeCount++; }
        }

        // The REAL package version for a type's owning assembly (e.g. TextMeshPro's actual installed version),
        // instead of a hardcoded guess. PackageInfo.FindForAssembly is available since 2019.2 — present on the 2019.4 floor, no guard needed.
        private static string PackageVersionForType(System.Type type)
        {
            if (type == null) return null;
            try { return UnityEditor.PackageManager.PackageInfo.FindForAssembly(type.Assembly)?.version; }
            catch { return null; }
        }

        /// <summary>
        /// Add Package Manager tools to the list
        /// </summary>
        private static void AddPackageManagerTools(List<object> tools, ref int installedCount, ref int activeCount, string category)
        {
            // TODO: query the live Package Manager API (UnityEditor.PackageManager.Client.List) instead of toolCache,
            // which currently holds no PM entries (the fabricated cache was removed in round-6 #6) — so no PM tools are
            // listed here yet. Deferred work; tracked in the roadmap (.claude/unity-mcp-fork-requirements.md).
            foreach (var tool in toolCache.Values)
            {
                if (!string.IsNullOrEmpty(category) && tool.Category != category)
                    continue;

                tools.Add(new
                {
                    name = tool.Name,
                    displayName = tool.DisplayName,
                    version = tool.Version,
                    category = tool.Category,
                    isInstalled = tool.IsInstalled,
                    isActive = tool.IsActive
                });

                if (tool.IsInstalled)
                {
                    installedCount++;
                    if (tool.IsActive) activeCount++;
                }
            }
        }

        /// <summary>
        /// Activate a tool
        /// </summary>
        private static HandlerOutcome ActivateTool(string toolName)
        {
            try
            {
                if (string.IsNullOrEmpty(toolName))
                {
                    return HandlerOutcome.Fail("Tool name not specified", "VALIDATION_ERROR");
                }

                // Check if tool exists
                var toolInfo = GetToolInfo(toolName);
                if (toolInfo == null)
                {
                    return HandlerOutcome.Fail($"Tool not found: {toolName}", "NOT_FOUND");
                }

                // Check if already active
                if (toolInfo.IsActive)
                {
                    return HandlerOutcome.Ok(new
                    {
                        success = true,
                        action = "activate",
                        toolName = toolName,
                        alreadyActive = true,
                        message = $"Tool is already active: {toolName}"
                    });
                }

                // Try to open the tool window
                bool activated = OpenToolWindow(toolName);

                if (activated)
                {
                    toolInfo.IsActive = true;
                    return HandlerOutcome.Ok(new
                    {
                        success = true,
                        action = "activate",
                        toolName = toolName,
                        previousState = new { isActive = false },
                        currentState = new { isActive = true },
                        message = $"Tool activated: {toolName}"
                    });
                }
                else
                {
                    return HandlerOutcome.Fail($"Failed to activate tool: {toolName}", "INVALID_STATE");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ToolManagementHandler] Error activating tool '{toolName}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to activate tool: {e.Message}");
            }
        }

        /// <summary>
        /// Deactivate a tool
        /// </summary>
        private static HandlerOutcome DeactivateTool(string toolName)
        {
            try
            {
                if (string.IsNullOrEmpty(toolName))
                {
                    return HandlerOutcome.Fail("Tool name not specified", "VALIDATION_ERROR");
                }

                // Check if tool exists
                var toolInfo = GetToolInfo(toolName);
                if (toolInfo == null)
                {
                    return HandlerOutcome.Fail($"Tool not found: {toolName}", "NOT_FOUND");
                }

                // Check if already inactive
                if (!toolInfo.IsActive)
                {
                    return HandlerOutcome.Ok(new
                    {
                        success = true,
                        action = "deactivate",
                        toolName = toolName,
                        alreadyInactive = true,
                        message = $"Tool is already inactive: {toolName}"
                    });
                }

                // Try to close the tool window
                bool deactivated = CloseToolWindow(toolName);

                if (deactivated)
                {
                    toolInfo.IsActive = false;
                    return HandlerOutcome.Ok(new
                    {
                        success = true,
                        action = "deactivate",
                        toolName = toolName,
                        previousState = new { isActive = true },
                        currentState = new { isActive = false },
                        message = $"Tool deactivated: {toolName}"
                    });
                }
                else
                {
                    return HandlerOutcome.Fail($"Failed to deactivate tool: {toolName}", "INVALID_STATE");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ToolManagementHandler] Error deactivating tool '{toolName}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to deactivate tool: {e.Message}");
            }
        }

        /// <summary>
        /// Refresh the tool cache
        /// </summary>
        private static HandlerOutcome RefreshToolCache()
        {
            try
            {
                UpdateToolCache();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "refresh",
                    message = "Tool cache refreshed",
                    toolsCount = toolCache.Count + 3, // +3 for built-in tools
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[ToolManagementHandler] Error refreshing tool cache: {e.Message}");
                return HandlerOutcome.Fail($"Failed to refresh tool cache: {e.Message}");
            }
        }

        /// <summary>
        /// Update the tool cache
        /// </summary>
        private static void UpdateToolCache()
        {
            toolCache.Clear();
            
            // The package-manager tool list here was previously FABRICATED (hardcoded "2D Sprite"/"Animation" with a
            // bogus "1.0.0" version). Removed — use list_packages for the real, complete installed-package list.
            // manage_tools reports only the built-in editor tools it can actually activate/deactivate. round-6 #6.
            lastCacheUpdate = DateTime.Now;
        }

        /// <summary>
        /// Get tool info by name
        /// </summary>
        private static ToolInfo GetToolInfo(string toolName)
        {
            // Built-in tools first, with REAL versions (round-6 #6).
            string typeName = null, displayName = toolName, category = null;
            switch (toolName)
            {
                case "ProBuilder": typeName = "UnityEngine.ProBuilder.ProBuilderMesh, Unity.ProBuilder"; displayName = "ProBuilder"; category = "Modeling"; break;
                case "Cinemachine": typeName = "Cinemachine.CinemachineVirtualCamera, Cinemachine"; displayName = "Cinemachine"; category = "Camera"; break;
                case "TextMeshPro": typeName = "TMPro.TextMeshProUGUI, Unity.TextMeshPro"; displayName = "TextMesh Pro"; category = "UI"; break;
            }
            if (typeName != null)
            {
                var type = System.Type.GetType(typeName);
                return new ToolInfo
                {
                    Name = toolName,
                    DisplayName = displayName,
                    Version = type != null ? (PackageVersionForType(type) ?? "unknown") : "Not installed",
                    Category = category,
                    IsInstalled = type != null,
                    IsActive = IsToolWindowOpen(toolName)
                };
            }
            return toolCache.ContainsKey(toolName) ? toolCache[toolName] : null;
        }

        /// <summary>
        /// Check if a tool window is open
        /// </summary>
        private static bool IsToolWindowOpen(string toolName)
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            return windows.Any(w => w.GetType().Name.Contains(toolName));
        }

        // Name-based EditorWindow helpers: reference windows that are internal on some floors (e.g. AnimationWindow
        // is internal on 2019.4) by full type name, so the same code compiles on every supported version.
        private static bool IsEditorWindowOpen(string fullTypeName)
        {
            return Resources.FindObjectsOfTypeAll<EditorWindow>().Any(w => w.GetType().FullName == fullTypeName);
        }

        private static System.Type FindEditorWindowType(string fullTypeName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullTypeName);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// Open a tool window
        /// </summary>
        private static bool OpenToolWindow(string toolName)
        {
            try
            {
                switch (toolName)
                {
                    case "ProBuilder":
                        EditorApplication.ExecuteMenuItem("Tools/ProBuilder/ProBuilder Window");
                        return true;
                    case "Cinemachine":
                        // Cinemachine doesn't have a dedicated window, it uses components
                        return true;
                    case "Animation":
                    {
                        // AnimationWindow is internal on the 2019.4 floor — reference it by name (works on every
                        // supported version) instead of the typed API. (COMPATIBILITY.md)
                        var animType = FindEditorWindowType("UnityEditor.AnimationWindow");
                        if (animType != null) { EditorWindow.GetWindow(animType).Show(); return true; }
                        return false;
                    }
                    default:
                        // Try generic menu item
                        return EditorApplication.ExecuteMenuItem($"Window/{toolName}");
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Close a tool window
        /// </summary>
        private static bool CloseToolWindow(string toolName)
        {
            try
            {
                var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                var toolWindow = windows.FirstOrDefault(w => w.GetType().Name.Contains(toolName));
                
                if (toolWindow != null)
                {
                    toolWindow.Close();
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tool information class
        /// </summary>
        private class ToolInfo
        {
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public string Version { get; set; }
            public string Category { get; set; }
            public bool IsInstalled { get; set; }
            public bool IsActive { get; set; }
        }
    }
}