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

        // Error as an opaque { error } payload — ResponseClassifier classifies it as a real error
        // envelope on the wire (no inline status key needed).
        private static JObject Err(string error) => new JObject { ["error"] = error };
    }
}
