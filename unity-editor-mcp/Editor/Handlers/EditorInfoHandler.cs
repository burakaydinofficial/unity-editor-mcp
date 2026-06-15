using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Editor / project operations: environment info, project settings (read + write), the installed
    /// UPM package set, package add/remove, and editor lifecycle. On the Core CommandDispatcher rail —
    /// every method returns a HandlerOutcome (Ok/Fail), so an error can never serialize as a success.
    /// Every API used here is floor-safe (Unity 2020.3+); packages are read from the manifest/lock files
    /// (synchronous, version-independent) rather than the async PackageManager.Client API.
    /// </summary>
    public static class EditorInfoHandler
    {
        private static string ProjectRoot()
        {
            // Application.dataPath is "<project>/Assets"; the project root is its parent.
            return Directory.GetParent(Application.dataPath)?.FullName?.Replace("\\", "/");
        }

        public static HandlerOutcome GetEditorInfo(JObject parameters)
        {
            try
            {
                return HandlerOutcome.Ok(new JObject
                {
                    ["unityVersion"] = Application.unityVersion,
                    ["platform"] = Application.platform.ToString(),
                    ["systemLanguage"] = Application.systemLanguage.ToString(),
                    ["projectPath"] = ProjectRoot(),
                    ["dataPath"] = Application.dataPath,
                    ["productName"] = Application.productName,
                    ["companyName"] = Application.companyName,
                    ["version"] = Application.version,
                    ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    ["isPlaying"] = EditorApplication.isPlaying,
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["applicationPath"] = EditorApplication.applicationPath
                });
            }
            catch (Exception e) { return Err($"Error getting editor info: {e.Message}"); }
        }

        public static HandlerOutcome GetProjectSettings(JObject parameters)
        {
            try
            {
                var group = EditorUserBuildSettings.selectedBuildTargetGroup;
                return HandlerOutcome.Ok(new JObject
                {
                    ["productName"] = PlayerSettings.productName,
                    ["companyName"] = PlayerSettings.companyName,
                    ["bundleVersion"] = PlayerSettings.bundleVersion,
                    ["colorSpace"] = PlayerSettings.colorSpace.ToString(),
                    ["defaultScreenWidth"] = PlayerSettings.defaultScreenWidth,
                    ["defaultScreenHeight"] = PlayerSettings.defaultScreenHeight,
                    ["runInBackground"] = PlayerSettings.runInBackground,
                    ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    ["selectedBuildTargetGroup"] = group.ToString(),
                    // Group-keyed getters: the BuildTargetGroup overloads are the floor-safe ones
                    // (the NamedBuildTarget overloads are 2021.2+). See COMPATIBILITY.md.
                    ["scriptingBackend"] = PlayerSettings.GetScriptingBackend(group).ToString(),
                    ["apiCompatibilityLevel"] = PlayerSettings.GetApiCompatibilityLevel(group).ToString(),
                    ["scriptingDefineSymbols"] = PlayerSettings.GetScriptingDefineSymbolsForGroup(group)
                });
            }
            catch (Exception e) { return Err($"Error getting project settings: {e.Message}"); }
        }

        public static HandlerOutcome ListPackages(JObject parameters)
        {
            try
            {
                var root = ProjectRoot();
                var manifestPath = Path.Combine(root, "Packages", "manifest.json");
                var lockPath = Path.Combine(root, "Packages", "packages-lock.json");

                // Directly-requested dependencies: { name: "version-or-git-url" }.
                var dependencies = new JObject();
                if (File.Exists(manifestPath))
                {
                    var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                    if (manifest["dependencies"] is JObject deps) dependencies = deps;
                }

                // Full resolved set (incl. transitive + built-in modules) with source, from the lock file.
                var resolved = new JArray();
                if (File.Exists(lockPath))
                {
                    var lockFile = JObject.Parse(File.ReadAllText(lockPath));
                    if (lockFile["dependencies"] is JObject lockDeps)
                    {
                        foreach (var kv in lockDeps)
                        {
                            var info = kv.Value as JObject;
                            resolved.Add(new JObject
                            {
                                ["name"] = kv.Key,
                                ["version"] = info?["version"],
                                ["source"] = info?["source"]
                            });
                        }
                    }
                }

                return HandlerOutcome.Ok(new JObject
                {
                    ["dependencies"] = dependencies,
                    ["resolved"] = resolved,
                    ["count"] = resolved.Count
                });
            }
            catch (Exception e) { return Err($"Error listing packages: {e.Message}"); }
        }

        public static HandlerOutcome SetProjectSetting(JObject parameters)
        {
            try
            {
                var key = parameters["key"]?.ToString();
                if (string.IsNullOrEmpty(key)) return Err("Missing required parameter: key", "VALIDATION_ERROR");
                var value = parameters["value"];
                if (value == null || value.Type == JTokenType.Null) return Err("Missing required parameter: value", "VALIDATION_ERROR");

                var group = EditorUserBuildSettings.selectedBuildTargetGroup;
                switch (key)
                {
                    case "productName": PlayerSettings.productName = value.ToString(); break;
                    case "companyName": PlayerSettings.companyName = value.ToString(); break;
                    case "bundleVersion": PlayerSettings.bundleVersion = value.ToString(); break;
                    case "defaultScreenWidth":
                    {
                        if (!TryGetPositiveInt(value, out var w, out var err)) return Err($"defaultScreenWidth {err}", "VALIDATION_ERROR");
                        PlayerSettings.defaultScreenWidth = w;
                        break;
                    }
                    case "defaultScreenHeight":
                    {
                        if (!TryGetPositiveInt(value, out var h, out var err)) return Err($"defaultScreenHeight {err}", "VALIDATION_ERROR");
                        PlayerSettings.defaultScreenHeight = h;
                        break;
                    }
                    case "runInBackground":
                        if (value.Type != JTokenType.Boolean) return Err("runInBackground must be a boolean", "VALIDATION_ERROR");
                        PlayerSettings.runInBackground = value.ToObject<bool>(); break;
                    case "colorSpace": PlayerSettings.colorSpace = (ColorSpace)Enum.Parse(typeof(ColorSpace), value.ToString(), true); break;
                    case "scriptingDefineSymbols": PlayerSettings.SetScriptingDefineSymbolsForGroup(group, value.ToString()); break;
                    default:
                        return Err($"Unsupported setting key: {key}. Supported: productName, companyName, bundleVersion, defaultScreenWidth, defaultScreenHeight, runInBackground, colorSpace, scriptingDefineSymbols.", "VALIDATION_ERROR");
                }
                AssetDatabase.SaveAssets();
                return HandlerOutcome.Ok(new JObject { ["message"] = $"Set project setting '{key}'.", ["key"] = key });
            }
            catch (Exception e) { return Err($"Error setting project setting: {e.Message}"); }
        }

        public static HandlerOutcome ManagePackages(JObject parameters)
        {
            try
            {
                var action = parameters["action"]?.ToString()?.ToLowerInvariant();
                if (string.IsNullOrEmpty(action)) return Err("Missing required parameter: action", "VALIDATION_ERROR");
                var packageId = parameters["packageId"]?.ToString();
                if (string.IsNullOrEmpty(packageId)) return Err("Missing required parameter: packageId", "VALIDATION_ERROR");

                switch (action)
                {
                    case "add":
                        // Fire-and-forget: the PackageManager resolves asynchronously and triggers a domain
                        // reload; the bridge reconnects on its own. Verify the outcome with list_packages.
                        UnityEditor.PackageManager.Client.Add(packageId);
                        return HandlerOutcome.Ok(new JObject
                        {
                            ["message"] = $"Requested add of '{packageId}'. Resolution is asynchronous (the editor will recompile/reload); verify with list_packages.",
                            ["action"] = "add",
                            ["packageId"] = packageId
                        });
                    case "remove":
                        UnityEditor.PackageManager.Client.Remove(packageId);
                        return HandlerOutcome.Ok(new JObject
                        {
                            ["message"] = $"Requested removal of '{packageId}'. Resolution is asynchronous; verify with list_packages.",
                            ["action"] = "remove",
                            ["packageId"] = packageId
                        });
                    default:
                        return Err($"Unknown action: {action}. Supported: add, remove.", "VALIDATION_ERROR");
                }
            }
            catch (Exception e) { return Err($"Error managing packages: {e.Message}"); }
        }

        public static HandlerOutcome QuitEditor(JObject parameters)
        {
            try
            {
                // Defer the exit several editor ticks so the success response is actually flushed over the
                // socket before the process dies. One tick is not enough for the async writer; an immediate
                // Exit drops the reply and looks like a crash to the client.
                int frames = 0;
                EditorApplication.CallbackFunction cb = null;
                cb = () =>
                {
                    if (++frames >= 10)
                    {
                        EditorApplication.update -= cb;
                        EditorApplication.Exit(0);
                    }
                };
                EditorApplication.update += cb;
                return HandlerOutcome.Ok(new JObject { ["message"] = "Editor quit scheduled (after response flush)." });
            }
            catch (Exception e) { return Err($"Error quitting editor: {e.Message}"); }
        }

        // Validates a JSON value as a positive whole number within int range (pixel dimensions etc.).
        // Total over the double domain: rejects non-numbers, NaN/Infinity, fractional values, zero/negative,
        // and out-of-range values (an unchecked (int) cast of an out-of-range double bit-truncates on Mono
        // rather than throwing, which would silently corrupt the setting).
        private static bool TryGetPositiveInt(JToken value, out int result, out string error)
        {
            result = 0;
            error = "must be a positive whole number within int range";
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) return false;
            var d = value.ToObject<double>();
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            if (Math.Abs(d % 1.0) > 1e-9) return false;
            if (d < 1 || d > int.MaxValue) return false;
            result = (int)d;
            return true;
        }

        private static HandlerOutcome Err(string error, string code = "INTERNAL_ERROR") => HandlerOutcome.Fail(error, code);
    }
}
