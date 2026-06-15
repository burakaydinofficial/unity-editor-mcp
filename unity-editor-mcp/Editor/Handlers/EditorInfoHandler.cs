using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Read-only editor / project introspection: environment info, project settings, and the
    /// installed UPM package set. Every API used here is floor-safe (Unity 2020.3+) and synchronous —
    /// packages are read from the manifest/lock files rather than the async PackageManager.Client API,
    /// so the call never blocks the main thread or depends on a newer API surface.
    /// </summary>
    public static class EditorInfoHandler
    {
        private static string ProjectRoot()
        {
            // Application.dataPath is "<project>/Assets"; the project root is its parent.
            return Directory.GetParent(Application.dataPath)?.FullName?.Replace("\\", "/");
        }

        public static JObject GetEditorInfo(JObject parameters)
        {
            try
            {
                return new JObject
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
                };
            }
            catch (Exception e) { return Err($"Error getting editor info: {e.Message}"); }
        }

        public static JObject GetProjectSettings(JObject parameters)
        {
            try
            {
                var group = EditorUserBuildSettings.selectedBuildTargetGroup;
                return new JObject
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
                };
            }
            catch (Exception e) { return Err($"Error getting project settings: {e.Message}"); }
        }

        public static JObject ListPackages(JObject parameters)
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

                return new JObject
                {
                    ["dependencies"] = dependencies,
                    ["resolved"] = resolved,
                    ["count"] = resolved.Count
                };
            }
            catch (Exception e) { return Err($"Error listing packages: {e.Message}"); }
        }

        public static JObject SetProjectSetting(JObject parameters)
        {
            try
            {
                var key = parameters["key"]?.ToString();
                if (string.IsNullOrEmpty(key)) return Err("Missing required parameter: key");
                var value = parameters["value"];
                if (value == null || value.Type == JTokenType.Null) return Err("Missing required parameter: value");

                var group = EditorUserBuildSettings.selectedBuildTargetGroup;
                switch (key)
                {
                    case "productName": PlayerSettings.productName = value.ToString(); break;
                    case "companyName": PlayerSettings.companyName = value.ToString(); break;
                    case "bundleVersion": PlayerSettings.bundleVersion = value.ToString(); break;
                    case "defaultScreenWidth":
                        if (value.Type != JTokenType.Integer) return Err("defaultScreenWidth must be an integer");
                        PlayerSettings.defaultScreenWidth = value.ToObject<int>(); break;
                    case "defaultScreenHeight":
                        if (value.Type != JTokenType.Integer) return Err("defaultScreenHeight must be an integer");
                        PlayerSettings.defaultScreenHeight = value.ToObject<int>(); break;
                    case "runInBackground":
                        if (value.Type != JTokenType.Boolean) return Err("runInBackground must be a boolean");
                        PlayerSettings.runInBackground = value.ToObject<bool>(); break;
                    case "colorSpace": PlayerSettings.colorSpace = (ColorSpace)Enum.Parse(typeof(ColorSpace), value.ToString(), true); break;
                    case "scriptingDefineSymbols": PlayerSettings.SetScriptingDefineSymbolsForGroup(group, value.ToString()); break;
                    default:
                        return Err($"Unsupported setting key: {key}. Supported: productName, companyName, bundleVersion, defaultScreenWidth, defaultScreenHeight, runInBackground, colorSpace, scriptingDefineSymbols.");
                }
                AssetDatabase.SaveAssets();
                return new JObject { ["message"] = $"Set project setting '{key}'.", ["key"] = key };
            }
            catch (Exception e) { return Err($"Error setting project setting: {e.Message}"); }
        }

        public static JObject ManagePackages(JObject parameters)
        {
            try
            {
                var action = parameters["action"]?.ToString()?.ToLowerInvariant();
                if (string.IsNullOrEmpty(action)) return Err("Missing required parameter: action");
                var packageId = parameters["packageId"]?.ToString();
                if (string.IsNullOrEmpty(packageId)) return Err("Missing required parameter: packageId");

                switch (action)
                {
                    case "add":
                        // Fire-and-forget: the PackageManager resolves asynchronously and triggers a domain
                        // reload; the bridge reconnects on its own. Verify the outcome with list_packages.
                        UnityEditor.PackageManager.Client.Add(packageId);
                        return new JObject
                        {
                            ["message"] = $"Requested add of '{packageId}'. Resolution is asynchronous (the editor will recompile/reload); verify with list_packages.",
                            ["action"] = "add",
                            ["packageId"] = packageId
                        };
                    case "remove":
                        UnityEditor.PackageManager.Client.Remove(packageId);
                        return new JObject
                        {
                            ["message"] = $"Requested removal of '{packageId}'. Resolution is asynchronous; verify with list_packages.",
                            ["action"] = "remove",
                            ["packageId"] = packageId
                        };
                    default:
                        return Err($"Unknown action: {action}. Supported: add, remove.");
                }
            }
            catch (Exception e) { return Err($"Error managing packages: {e.Message}"); }
        }

        public static JObject QuitEditor(JObject parameters)
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
                return new JObject { ["message"] = "Editor quit scheduled (after response flush)." };
            }
            catch (Exception e) { return Err($"Error quitting editor: {e.Message}"); }
        }

        // Error as an opaque { error } payload — ResponseClassifier classifies it as a real error
        // envelope on the wire (no inline status key needed).
        private static JObject Err(string error) => new JObject { ["error"] = error };
    }
}
