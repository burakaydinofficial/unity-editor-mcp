using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles Scene-related operations
    /// </summary>
    public static class SceneHandler
    {
        /// <summary>
        /// Creates a new scene based on parameters
        /// </summary>
        public static HandlerOutcome CreateScene(JObject parameters)
        {
            try
            {
                if (EditorApplication.isPlaying)
                    return HandlerOutcome.Fail("scene mutations refuse in play mode", "PLAY_MODE");
                // Get scene name (required)
                var sceneName = parameters["sceneName"]?.ToString();
                if (string.IsNullOrEmpty(sceneName))
                {
                    return HandlerOutcome.Fail("Scene name cannot be empty", "VALIDATION_ERROR");
                }

                // Validate scene name
                if (sceneName.Contains("/") || sceneName.Contains("\\") ||
                    sceneName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    return HandlerOutcome.Fail("Scene name contains invalid characters", "VALIDATION_ERROR");
                }

                // Get optional parameters
                var path = parameters["path"]?.ToString() ?? "Assets/Scenes/";
                var loadScene = parameters["loadScene"]?.ToObject<bool>() ?? true;
                var addToBuildSettings = parameters["addToBuildSettings"]?.ToObject<bool>() ?? false;

                // A common mistake: passing a .unity FILE path. `path` is the target FOLDER; the file is named from
                // sceneName. Reject clearly instead of building "Assets/Foo.unity/Foo.unity" and failing on save.
                if (path.EndsWith(".unity"))
                {
                    return HandlerOutcome.Fail("path must be a folder (e.g. \"Assets/Scenes/\"), not a .unity file — the scene file is named from sceneName.", "VALIDATION_ERROR");
                }

                // Ensure path ends with /
                if (!path.EndsWith("/"))
                {
                    path += "/";
                }

                // Validate path
                if (!path.StartsWith("Assets/"))
                {
                    return HandlerOutcome.Fail("Invalid path: Path must be within Assets folder", "VALIDATION_ERROR");
                }

                // Create full scene path
                var scenePath = path + sceneName + ".unity";

                // Containment: a `..` in `path` resolves OUTSIDE the project (StartsWith("Assets/") alone
                // does not block it). No Node-side handler guards this, so the editor is the sole gate.
                if (!PathSafety.IsWithinProject(scenePath))
                {
                    return HandlerOutcome.Fail("path must stay within the project root", "VALIDATION_ERROR");
                }

                // Check if scene already exists
                if (File.Exists(scenePath))
                {
                    return HandlerOutcome.Fail($"Scene with name \"{sceneName}\" already exists at path \"{scenePath}\"", "INVALID_STATE");
                }

                // Ensure directory exists
                var directory = Path.GetDirectoryName(scenePath);
                if (!AssetDatabase.IsValidFolder(directory))
                {
                    // Create directory structure
                    var folders = directory.Split('/');
                    var currentPath = folders[0]; // "Assets"
                    
                    for (int i = 1; i < folders.Length; i++)
                    {
                        var nextPath = currentPath + "/" + folders[i];
                        if (!AssetDatabase.IsValidFolder(nextPath))
                        {
                            AssetDatabase.CreateFolder(currentPath, folders[i]);
                        }
                        currentPath = nextPath;
                    }
                }

                // Create the scene
                var newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, 
                    loadScene ? NewSceneMode.Single : NewSceneMode.Additive);
                
                // Save the scene
                bool saved = EditorSceneManager.SaveScene(newScene, scenePath);
                if (!saved)
                {
                    return HandlerOutcome.Fail("Failed to save scene", "INTERNAL_ERROR");
                }

                // Add to build settings if requested
                int sceneIndex = -1;
                if (addToBuildSettings)
                {
                    var buildScenes = EditorBuildSettings.scenes.ToList();
                    buildScenes.Add(new EditorBuildSettingsScene(scenePath, true));
                    EditorBuildSettings.scenes = buildScenes.ToArray();
                    sceneIndex = buildScenes.Count - 1;
                }

                // If we didn't load the scene, we need to unload it
                if (!loadScene && newScene.IsValid())
                {
                    EditorSceneManager.CloseScene(newScene, true);
                }

                // Prepare result
                var result = new
                {
                    sceneName = sceneName,
                    path = scenePath,
                    sceneIndex = sceneIndex,
                    isLoaded = loadScene,
                    summary = GenerateSummary(sceneName, scenePath, loadScene, sceneIndex)
                };

                return HandlerOutcome.Ok(result);
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to create scene: {ex.Message}");
            }
        }

        private static string GenerateSummary(string sceneName, string path, bool isLoaded, int sceneIndex)
        {
            var summary = isLoaded ? "Created and loaded" : "Created";
            summary += $" scene \"{sceneName}\" at \"{path}\"";
            
            if (sceneIndex >= 0)
            {
                summary += $" (build index: {sceneIndex})";
            }
            else if (!isLoaded)
            {
                summary += " (not loaded)";
            }

            return summary;
        }

        /// <summary>
        /// Loads an existing scene based on parameters
        /// </summary>
        public static HandlerOutcome LoadScene(JObject parameters)
        {
            try
            {
                if (EditorApplication.isPlaying)
                    return HandlerOutcome.Fail("scene mutations refuse in play mode", "PLAY_MODE");
                // Get scene identification parameters
                var scenePath = parameters["scenePath"]?.ToString();
                var sceneName = parameters["sceneName"]?.ToString();
                
                // Validate that either scenePath or sceneName is provided
                if (string.IsNullOrEmpty(scenePath) && string.IsNullOrEmpty(sceneName))
                {
                    return HandlerOutcome.Fail("Either scenePath or sceneName must be provided", "VALIDATION_ERROR");
                }

                // Validate that only one is provided
                if (!string.IsNullOrEmpty(scenePath) && !string.IsNullOrEmpty(sceneName))
                {
                    return HandlerOutcome.Fail("Provide either scenePath or sceneName, not both", "VALIDATION_ERROR");
                }

                // Get load mode
                var loadModeStr = parameters["loadMode"]?.ToString() ?? "Single";

                // Validate load mode
                if (loadModeStr != "Single" && loadModeStr != "Additive")
                {
                    return HandlerOutcome.Fail("Invalid load mode. Must be 'Single' or 'Additive'", "VALIDATION_ERROR");
                }
                
                LoadSceneMode loadMode = loadModeStr == "Single" ? LoadSceneMode.Single : LoadSceneMode.Additive;
                
                // Store previous scene info (for Single mode)
                string previousSceneName = SceneManager.GetActiveScene().name;
                
                Scene loadedScene;
                string actualScenePath = scenePath;
                
                // Load by path
                if (!string.IsNullOrEmpty(scenePath))
                {
                    // Containment: scenePath is wholly caller-controlled and otherwise unchecked — block a
                    // `..`/absolute path from reading or opening a .unity file outside the project.
                    if (!PathSafety.IsWithinProject(scenePath))
                    {
                        return HandlerOutcome.Fail("scenePath must stay within the project root", "VALIDATION_ERROR");
                    }
                    // Check if file exists
                    if (!File.Exists(scenePath))
                    {
                        return HandlerOutcome.Fail($"Scene file not found: {scenePath}", "NOT_FOUND");
                    }

                    // Load the scene
                    loadedScene = EditorSceneManager.OpenScene(scenePath, loadMode == LoadSceneMode.Single ?
                        OpenSceneMode.Single : OpenSceneMode.Additive);
                }
                else // Load by name
                {
                    // Check if scene is in build settings
                    var buildScenes = EditorBuildSettings.scenes;
                    var sceneInBuild = buildScenes.FirstOrDefault(s => 
                        Path.GetFileNameWithoutExtension(s.path) == sceneName && s.enabled);
                    
                    if (sceneInBuild == null)
                    {
                        return HandlerOutcome.Fail($"Scene \"{sceneName}\" is not in build settings. Add it to build settings or load by path.", "NOT_FOUND");
                    }
                    
                    actualScenePath = sceneInBuild.path;
                    
                    // Load the scene
                    loadedScene = EditorSceneManager.OpenScene(actualScenePath, loadMode == LoadSceneMode.Single ? 
                        OpenSceneMode.Single : OpenSceneMode.Additive);
                }
                
                // Verify scene was loaded
                if (!loadedScene.IsValid())
                {
                    return HandlerOutcome.Fail("Failed to load scene", "INTERNAL_ERROR");
                }
                
                // Get active scene count (for additive mode)
                int activeSceneCount = SceneManager.sceneCount;
                
                // Prepare result
                var result = new
                {
                    sceneName = loadedScene.name,
                    scenePath = actualScenePath,
                    loadMode = loadModeStr,
                    isLoaded = loadedScene.isLoaded,
                    previousScene = loadMode == LoadSceneMode.Single ? previousSceneName : null,
                    activeSceneCount = loadMode == LoadSceneMode.Additive ? activeSceneCount : (int?)null,
                    summary = GenerateLoadSummary(loadedScene.name, loadModeStr, activeSceneCount)
                };
                
                return HandlerOutcome.Ok(result);
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to load scene: {ex.Message}");
            }
        }
        
        private static string GenerateLoadSummary(string sceneName, string loadMode, int activeSceneCount)
        {
            var summary = $"Loaded scene \"{sceneName}\" in {loadMode} mode";
            
            if (loadMode == "Additive" && activeSceneCount > 1)
            {
                summary += $" ({activeSceneCount} scenes active)";
            }
            
            return summary;
        }

        /// <summary>
        /// Saves the current scene
        /// </summary>
        public static HandlerOutcome SaveScene(JObject parameters)
        {
            try
            {
                if (EditorApplication.isPlaying)
                    return HandlerOutcome.Fail("scene mutations refuse in play mode", "PLAY_MODE");
                // Get current scene
                var currentScene = SceneManager.GetActiveScene();
                
                // Check if a scene is loaded
                if (!currentScene.IsValid())
                {
                    return HandlerOutcome.Fail("No scene is currently loaded", "INVALID_STATE");
                }
                
                // Get parameters
                var scenePath = parameters["scenePath"]?.ToString();
                var saveAs = parameters["saveAs"]?.ToObject<bool>() ?? false;
                
                // Validate saveAs requires scenePath
                if (saveAs && string.IsNullOrEmpty(scenePath))
                {
                    return HandlerOutcome.Fail("scenePath is required when saveAs is true", "VALIDATION_ERROR");
                }
                
                // Determine the save path
                string savePath = scenePath;
                string originalPath = currentScene.path;
                
                if (string.IsNullOrEmpty(savePath))
                {
                    // Save to current scene path
                    savePath = currentScene.path;
                    
                    // If scene has never been saved, require a path
                    if (string.IsNullOrEmpty(savePath))
                    {
                        return HandlerOutcome.Fail("Scene has never been saved. Please provide a scenePath.", "VALIDATION_ERROR");
                    }
                }
                else
                {
                    // Validate the provided path
                    if (!savePath.StartsWith("Assets/"))
                    {
                        return HandlerOutcome.Fail("Invalid path: Path must be within Assets folder", "VALIDATION_ERROR");
                    }

                    // Containment: StartsWith("Assets/") doesn't block `..`; a traversal scenePath would
                    // write the scene outside the project.
                    if (!PathSafety.IsWithinProject(savePath))
                    {
                        return HandlerOutcome.Fail("scenePath must stay within the project root", "VALIDATION_ERROR");
                    }

                    if (!savePath.EndsWith(".unity"))
                    {
                        return HandlerOutcome.Fail("Invalid path: Path must end with .unity", "VALIDATION_ERROR");
                    }
                    
                    // Ensure directory exists
                    var directory = Path.GetDirectoryName(savePath);
                    if (!AssetDatabase.IsValidFolder(directory))
                    {
                        // Create directory structure
                        var folders = directory.Split('/');
                        var currentPath = folders[0]; // "Assets"
                        
                        for (int i = 1; i < folders.Length; i++)
                        {
                            var nextPath = currentPath + "/" + folders[i];
                            if (!AssetDatabase.IsValidFolder(nextPath))
                            {
                                AssetDatabase.CreateFolder(currentPath, folders[i]);
                            }
                            currentPath = nextPath;
                        }
                    }
                }
                
                // Check if scene is dirty (has unsaved changes)
                bool wasDirty = currentScene.isDirty;
                
                // Save the scene
                bool saved = false;
                if (saveAs && !string.IsNullOrEmpty(scenePath))
                {
                    // Save as new scene
                    saved = EditorSceneManager.SaveScene(currentScene, savePath, true);
                }
                else
                {
                    // Regular save
                    saved = EditorSceneManager.SaveScene(currentScene, savePath);
                }
                
                if (!saved)
                {
                    return HandlerOutcome.Fail("Failed to save scene", "INTERNAL_ERROR");
                }

                // Prepare result
                var result = new
                {
                    sceneName = currentScene.name,
                    scenePath = savePath,
                    originalPath = saveAs ? originalPath : null,
                    saved = saved,
                    isDirty = false, // After saving, scene is no longer dirty
                    summary = GenerateSaveSummary(currentScene.name, savePath, saveAs, wasDirty)
                };
                
                return HandlerOutcome.Ok(result);
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to save scene: {ex.Message}");
            }
        }
        
        private static string GenerateSaveSummary(string sceneName, string savePath, bool saveAs, bool wasDirty)
        {
            if (!wasDirty && !saveAs)
            {
                return $"Scene \"{sceneName}\" has no unsaved changes";
            }
            
            var summary = saveAs ? "Saved scene" : "Saved scene";
            summary += $" \"{sceneName}\"";
            
            if (saveAs)
            {
                summary += $" as \"{savePath}\"";
            }
            else
            {
                summary += $" to \"{savePath}\"";
            }
            
            return summary;
        }

        /// <summary>
        /// Lists all scenes in the project
        /// </summary>
        public static HandlerOutcome ListScenes(JObject parameters)
        {
            try
            {
                // Get filter parameters
                var includeLoadedOnly = parameters["includeLoadedOnly"]?.ToObject<bool>() ?? false;
                var includeBuildScenesOnly = parameters["includeBuildScenesOnly"]?.ToObject<bool>() ?? false;
                var includePath = parameters["includePath"]?.ToString();
                
                var sceneList = new System.Collections.Generic.List<object>();
                
                // Get all scene files in the project
                var allSceneGuids = AssetDatabase.FindAssets("t:Scene");
                var allScenePaths = new System.Collections.Generic.List<string>();
                
                foreach (var guid in allSceneGuids)
                {
                    var scenePath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(scenePath) && scenePath.EndsWith(".unity"))
                    {
                        allScenePaths.Add(scenePath);
                    }
                }
                
                // Get build scenes
                var buildScenes = EditorBuildSettings.scenes;
                var buildScenePaths = new System.Collections.Generic.HashSet<string>();
                foreach (var buildScene in buildScenes)
                {
                    if (buildScene.enabled)
                    {
                        buildScenePaths.Add(buildScene.path);
                    }
                }
                
                // Get loaded scenes
                var loadedScenes = new System.Collections.Generic.HashSet<string>();
                var activeScenePath = SceneManager.GetActiveScene().path;
                
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded)
                    {
                        loadedScenes.Add(scene.path);
                    }
                }
                
                // Process each scene
                foreach (var scenePath in allScenePaths)
                {
                    // Apply filters
                    if (includeLoadedOnly && !loadedScenes.Contains(scenePath))
                        continue;
                        
                    if (includeBuildScenesOnly && !buildScenePaths.Contains(scenePath))
                        continue;
                        
                    if (!string.IsNullOrEmpty(includePath) && !scenePath.Contains(includePath))
                        continue;
                    
                    // Get scene info
                    var sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
                    var isLoaded = loadedScenes.Contains(scenePath);
                    var isActive = scenePath == activeScenePath;
                    
                    // Find build index
                    int buildIndex = -1;
                    for (int i = 0; i < buildScenes.Length; i++)
                    {
                        if (buildScenes[i].path == scenePath && buildScenes[i].enabled)
                        {
                            buildIndex = i;
                            break;
                        }
                    }
                    
                    sceneList.Add(new
                    {
                        name = sceneName,
                        path = scenePath,
                        buildIndex = buildIndex,
                        isLoaded = isLoaded,
                        isActive = isActive
                    });
                }
                
                // Count statistics
                int loadedCount = sceneList.Count(s => ((dynamic)s).isLoaded);
                int inBuildCount = sceneList.Count(s => ((dynamic)s).buildIndex >= 0);
                
                // Prepare result
                var result = new
                {
                    scenes = sceneList,
                    totalCount = sceneList.Count,
                    loadedCount = loadedCount,
                    inBuildCount = inBuildCount,
                    summary = GenerateListSummary(sceneList.Count, loadedCount, inBuildCount, 
                        includeLoadedOnly, includeBuildScenesOnly, includePath)
                };
                
                return HandlerOutcome.Ok(result);
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to list scenes: {ex.Message}");
            }
        }
        
        private static string GenerateListSummary(int totalCount, int loadedCount, int inBuildCount,
            bool includeLoadedOnly, bool includeBuildScenesOnly, string includePath)
        {
            if (totalCount == 0)
            {
                return "No scenes found";
            }
            
            var summary = $"Found {totalCount} scene{(totalCount != 1 ? "s" : "")}";
            
            if (includeLoadedOnly)
            {
                summary = $"Found {totalCount} loaded scene{(totalCount != 1 ? "s" : "")}";
            }
            else if (includeBuildScenesOnly)
            {
                summary = $"Found {totalCount} scene{(totalCount != 1 ? "s" : "")} in build settings";
            }
            else if (!string.IsNullOrEmpty(includePath))
            {
                summary = $"Found {totalCount} scene{(totalCount != 1 ? "s" : "")} matching path \"{includePath}\"";
            }
            else
            {
                // Add counts for unfiltered results
                summary += $" ({loadedCount} loaded, {inBuildCount} in build settings)";
            }
            
            return summary;
        }

        /// <summary>
        /// Gets detailed information about a scene
        /// </summary>
        public static HandlerOutcome GetSceneInfo(JObject parameters)
        {
            try
            {
                // Get scene identification parameters
                var scenePath = parameters["scenePath"]?.ToString();
                var sceneName = parameters["sceneName"]?.ToString();
                var includeGameObjects = parameters["includeGameObjects"]?.ToObject<bool>() ?? false;
                
                // Validate that only one identifier is provided if any
                if (!string.IsNullOrEmpty(scenePath) && !string.IsNullOrEmpty(sceneName))
                {
                    return HandlerOutcome.Fail("Provide either scenePath or sceneName, not both", "VALIDATION_ERROR");
                }

                // Containment: a caller scenePath with `..` would let File.Exists/FileInfo probe a file
                // outside the project (metadata disclosure).
                if (!string.IsNullOrEmpty(scenePath) && !PathSafety.IsWithinProject(scenePath))
                {
                    return HandlerOutcome.Fail("scenePath must stay within the project root", "VALIDATION_ERROR");
                }

                Scene targetScene;
                string targetScenePath = scenePath;
                
                // Determine which scene to get info about
                if (string.IsNullOrEmpty(scenePath) && string.IsNullOrEmpty(sceneName))
                {
                    // Get info about current scene
                    targetScene = SceneManager.GetActiveScene();
                    targetScenePath = targetScene.path;
                }
                else if (!string.IsNullOrEmpty(scenePath))
                {
                    // Find scene by path
                    targetScene = default(Scene);
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (scene.path == scenePath)
                        {
                            targetScene = scene;
                            break;
                        }
                    }
                }
                else // Find by name
                {
                    // First try loaded scenes
                    targetScene = SceneManager.GetSceneByName(sceneName);
                    
                    // If not loaded, find in project
                    if (!targetScene.IsValid())
                    {
                        var allSceneGuids = AssetDatabase.FindAssets("t:Scene");
                        foreach (var guid in allSceneGuids)
                        {
                            var path = AssetDatabase.GUIDToAssetPath(guid);
                            if (Path.GetFileNameWithoutExtension(path) == sceneName)
                            {
                                targetScenePath = path;
                                break;
                            }
                        }
                        
                        if (string.IsNullOrEmpty(targetScenePath))
                        {
                            return HandlerOutcome.Fail($"Scene not found: {sceneName}", "NOT_FOUND");
                        }
                    }
                    else
                    {
                        targetScenePath = targetScene.path;
                    }
                }
                
                // Validate scene path exists
                if (!targetScene.IsValid() && !string.IsNullOrEmpty(targetScenePath))
                {
                    if (!File.Exists(targetScenePath))
                    {
                        return HandlerOutcome.Fail($"Scene not found: {targetScenePath}", "NOT_FOUND");
                    }
                }
                
                // Gather scene information
                var sceneInfo = new System.Collections.Generic.Dictionary<string, object>();
                
                sceneInfo["sceneName"] = targetScene.IsValid() ? targetScene.name : Path.GetFileNameWithoutExtension(targetScenePath);
                sceneInfo["scenePath"] = targetScenePath;
                sceneInfo["isLoaded"] = targetScene.IsValid() && targetScene.isLoaded;
                sceneInfo["isActive"] = targetScene.IsValid() && SceneManager.GetActiveScene() == targetScene;
                sceneInfo["isDirty"] = targetScene.IsValid() && targetScene.isDirty;
                
                // Get build index
                int buildIndex = -1;
                var buildScenes = EditorBuildSettings.scenes;
                for (int i = 0; i < buildScenes.Length; i++)
                {
                    if (buildScenes[i].path == targetScenePath && buildScenes[i].enabled)
                    {
                        buildIndex = i;
                        break;
                    }
                }
                sceneInfo["buildIndex"] = buildIndex;
                
                // Get file info if scene exists on disk
                if (!string.IsNullOrEmpty(targetScenePath) && File.Exists(targetScenePath))
                {
                    var fileInfo = new FileInfo(targetScenePath);
                    sceneInfo["fileSize"] = fileInfo.Length;
                    sceneInfo["lastModified"] = fileInfo.LastWriteTimeUtc.ToString("o");
                }
                
                // Get GameObject info if scene is loaded and requested
                if (targetScene.IsValid() && targetScene.isLoaded && includeGameObjects)
                {
                    var rootObjects = targetScene.GetRootGameObjects();
                    var rootObjectList = new System.Collections.Generic.List<object>();
                    int totalObjectCount = 0;
                    
                    foreach (var obj in rootObjects)
                    {
                        int childCount = CountChildren(obj.transform);
                        totalObjectCount += childCount + 1; // Include the root object itself
                        
                        rootObjectList.Add(new
                        {
                            name = obj.name,
                            childCount = childCount
                        });
                    }
                    
                    sceneInfo["rootGameObjects"] = rootObjectList;
                    sceneInfo["rootObjectCount"] = rootObjects.Length;
                    sceneInfo["totalObjectCount"] = totalObjectCount;
                }
                else if (includeGameObjects && (!targetScene.IsValid() || !targetScene.isLoaded))
                {
                    // Can't get GameObject info for unloaded scenes
                    sceneInfo["rootGameObjects"] = new object[0];
                    sceneInfo["rootObjectCount"] = 0;
                    sceneInfo["totalObjectCount"] = 0;
                }
                
                // Generate summary
                sceneInfo["summary"] = GenerateSceneInfoSummary(sceneInfo);
                
                return HandlerOutcome.Ok(sceneInfo);
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to get scene info: {ex.Message}");
            }
        }
        
        private static int CountChildren(Transform transform)
        {
            int count = transform.childCount;
            foreach (Transform child in transform)
            {
                count += CountChildren(child);
            }
            return count;
        }
        
        private static string GenerateSceneInfoSummary(System.Collections.Generic.Dictionary<string, object> info)
        {
            var sceneName = info["sceneName"].ToString();
            var isLoaded = (bool)info["isLoaded"];
            var isActive = (bool)info["isActive"];
            var isDirty = (bool)info["isDirty"];
            var buildIndex = (int)info["buildIndex"];
            
            var summary = $"Scene \"{sceneName}\" - ";
            
            if (isLoaded)
            {
                summary += isActive ? "Loaded and active" : "Loaded";
                if (isDirty)
                {
                    summary += " (unsaved changes)";
                }
            }
            else
            {
                summary += "Not loaded";
            }
            
            if (buildIndex >= 0)
            {
                summary += $", in build settings (index: {buildIndex})";
            }
            else
            {
                summary += ", not in build settings";
            }
            
            // Add file size if available
            if (info.ContainsKey("fileSize"))
            {
                var fileSize = (long)info["fileSize"];
                var sizeStr = FormatFileSize(fileSize);
                summary += $", {sizeStr}";
            }
            
            // Add GameObject count if available
            if (info.ContainsKey("totalObjectCount"))
            {
                var totalCount = (int)info["totalObjectCount"];
                summary += $", {totalCount} total GameObjects";
            }
            
            return summary;
        }
        
        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.#} {sizes[order]}";
        }

        // ===== Build-settings scene-list management (E-tail) — manage_build_settings =====
        public static HandlerOutcome ManageBuildSettings(JObject parameters)
        {
            try
            {
                string action = (parameters["action"]?.ToString() ?? "list").ToLowerInvariant();
                var scenes = EditorBuildSettings.scenes.ToList();

                switch (action)
                {
                    case "list":
                        return HandlerOutcome.Ok(BuildSceneList(scenes));

                    case "add":
                    {
                        string scenePath = NormalizeScenePath(parameters["scenePath"]?.ToString());
                        if (string.IsNullOrEmpty(scenePath))
                            return HandlerOutcome.Fail("scenePath is required for add", "VALIDATION_ERROR");
                        var guard = PathSafety.Guard(scenePath, "scenePath");
                        if (guard != null) return guard;
                        if (!scenePath.EndsWith(".unity") || !File.Exists(scenePath))
                            return HandlerOutcome.Fail($"Scene not found (must be an existing .unity in the project): {scenePath}", "NOT_FOUND");
                        if (scenes.Any(s => s.path == scenePath))
                            return HandlerOutcome.Fail($"Scene already in build settings: {scenePath}", "ALREADY_EXISTS");
                        var entry = new EditorBuildSettingsScene(scenePath, parameters["enabled"]?.ToObject<bool>() ?? true);
                        int? index = parameters["index"]?.ToObject<int?>();
                        if (index.HasValue && index.Value >= 0 && index.Value < scenes.Count) scenes.Insert(index.Value, entry);
                        else scenes.Add(entry);
                        EditorBuildSettings.scenes = scenes.ToArray();
                        return HandlerOutcome.Ok(BuildSceneList(scenes, $"Added {scenePath}"));
                    }

                    case "remove":
                    {
                        int idx = ResolveBuildIndex(parameters, scenes, out string err);
                        if (err != null) return HandlerOutcome.Fail(err, idx == -2 ? "VALIDATION_ERROR" : "NOT_FOUND");
                        string removed = scenes[idx].path;
                        scenes.RemoveAt(idx);
                        EditorBuildSettings.scenes = scenes.ToArray();
                        return HandlerOutcome.Ok(BuildSceneList(scenes, $"Removed {removed}"));
                    }

                    case "move":
                    {
                        int idx = ResolveBuildIndex(parameters, scenes, out string err);
                        if (err != null) return HandlerOutcome.Fail(err, idx == -2 ? "VALIDATION_ERROR" : "NOT_FOUND");
                        int? to = parameters["toIndex"]?.ToObject<int?>();
                        if (!to.HasValue) return HandlerOutcome.Fail("toIndex is required for move", "VALIDATION_ERROR");
                        int dest = Math.Max(0, Math.Min(to.Value, scenes.Count - 1));
                        var entry = scenes[idx];
                        scenes.RemoveAt(idx);
                        scenes.Insert(dest, entry);
                        EditorBuildSettings.scenes = scenes.ToArray();
                        return HandlerOutcome.Ok(BuildSceneList(scenes, $"Moved {entry.path} to index {dest}"));
                    }

                    case "set_enabled":
                    {
                        int idx = ResolveBuildIndex(parameters, scenes, out string err);
                        if (err != null) return HandlerOutcome.Fail(err, idx == -2 ? "VALIDATION_ERROR" : "NOT_FOUND");
                        bool? enabled = parameters["enabled"]?.ToObject<bool?>();
                        if (!enabled.HasValue) return HandlerOutcome.Fail("enabled (bool) is required for set_enabled", "VALIDATION_ERROR");
                        string path = scenes[idx].path;
                        scenes[idx] = new EditorBuildSettingsScene(path, enabled.Value);
                        EditorBuildSettings.scenes = scenes.ToArray();
                        return HandlerOutcome.Ok(BuildSceneList(scenes, $"{(enabled.Value ? "Enabled" : "Disabled")} {path}"));
                    }

                    case "clear":
                        EditorBuildSettings.scenes = new EditorBuildSettingsScene[0];
                        return HandlerOutcome.Ok(BuildSceneList(new List<EditorBuildSettingsScene>(), "Cleared build scene list"));

                    default:
                        return HandlerOutcome.Fail($"Unknown action '{action}'. Use list, add, remove, move, set_enabled, or clear.", "VALIDATION_ERROR");
                }
            }
            catch (Exception e)
            {
                return HandlerOutcome.Fail($"manage_build_settings failed: {e.Message}");
            }
        }

        // Resolves a build-scene index from `index` or `scenePath`. Sets err + returns -2 (validation) / -1 (not found).
        private static int ResolveBuildIndex(JObject p, List<EditorBuildSettingsScene> scenes, out string err)
        {
            err = null;
            int? index = p["index"]?.ToObject<int?>();
            if (index.HasValue)
            {
                if (index.Value < 0 || index.Value >= scenes.Count) { err = $"index {index.Value} out of range (0..{scenes.Count - 1})"; return -2; }
                return index.Value;
            }
            string scenePath = NormalizeScenePath(p["scenePath"]?.ToString());
            if (string.IsNullOrEmpty(scenePath)) { err = "scenePath or index is required"; return -2; }
            int i = scenes.FindIndex(s => s.path == scenePath);
            if (i < 0) { err = $"Scene not in build settings: {scenePath}"; return -1; }
            return i;
        }

        private static object BuildSceneList(List<EditorBuildSettingsScene> scenes, string message = null)
        {
            var list = new List<object>();
            for (int i = 0; i < scenes.Count; i++)
                list.Add(new { index = i, path = scenes[i].path, enabled = scenes[i].enabled, exists = File.Exists(scenes[i].path) });
            return new
            {
                scenes = list,
                count = scenes.Count,
                enabledCount = scenes.Count(s => s.enabled),
                message = message ?? $"{scenes.Count} build scene(s)"
            };
        }

        // Canonicalize a scene path to project-relative ("Assets/..."), so the duplicate check, FindIndex, and
        // EditorBuildSettingsScene all use the form Unity stores. An absolute in-project path becomes relative;
        // anything else is returned as-is (then caught by PathSafety / File.Exists).
        private static string NormalizeScenePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            p = p.Replace('\\', '/');
            if (p.StartsWith("Assets/")) return p;
            var rel = FileUtil.GetProjectRelativePath(p);
            return string.IsNullOrEmpty(rel) ? p : rel;
        }

        // Count OPEN-AND-LOADED scenes manually. The loaded-scene-count convenience property is NOT on the
        // 2019.4–2020.3 floor (added later); only sceneCount + GetSceneAt + Scene.isLoaded are floor-ancient.
        // Caught by a cold batch compile on the floor (an interactive incremental compile had masked it).
        private static int CountLoadedScenes()
        {
            int n = 0;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                if (EditorSceneManager.GetSceneAt(i).isLoaded) n++;
            return n;
        }

        // ===== close_scene — selectively unload one open scene (vs load_scene Single, which closes ALL + reloads) =====
        public static HandlerOutcome CloseScene(JObject parameters)
        {
            try
            {
                string scenePath = parameters["scenePath"]?.ToString();
                string sceneName = parameters["sceneName"]?.ToString();
                if (string.IsNullOrEmpty(scenePath) && string.IsNullOrEmpty(sceneName))
                    return HandlerOutcome.Fail("scenePath or sceneName is required", "VALIDATION_ERROR");

                UnityEngine.SceneManagement.Scene target = default;
                bool found = false;
                for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                {
                    var s = EditorSceneManager.GetSceneAt(i);
                    if ((!string.IsNullOrEmpty(scenePath) && s.path == scenePath) ||
                        (!string.IsNullOrEmpty(sceneName) && s.name == sceneName))
                    { target = s; found = true; break; }
                }
                if (!found)
                    return HandlerOutcome.Fail($"Loaded scene not found: {scenePath ?? sceneName}", "NOT_FOUND");

                if (CountLoadedScenes() <= 1)
                    return HandlerOutcome.Fail("Cannot close the last loaded scene", "INVALID_STATE");

                if (target.isDirty)
                {
                    bool save = parameters["save"]?.ToObject<bool>() ?? false;
                    bool force = parameters["force"]?.ToObject<bool>() ?? false;
                    if (save) EditorSceneManager.SaveScene(target);
                    else if (!force)
                        return HandlerOutcome.Fail($"Scene '{target.name}' has unsaved changes — pass save:true to save, or force:true to discard.", "CONFIRMATION_REQUIRED");
                }

                bool removeScene = parameters["remove"]?.ToObject<bool>() ?? true;
                string closedPath = target.path;
                string closedName = target.name;
                bool ok = EditorSceneManager.CloseScene(target, removeScene);
                return HandlerOutcome.Ok(new
                {
                    success = ok,
                    closedScene = closedPath,
                    name = closedName,
                    removed = removeScene,
                    remainingLoaded = CountLoadedScenes(),
                    message = ok ? $"Closed scene: {closedName}" : $"Failed to close scene: {closedName}"
                });
            }
            catch (Exception e)
            {
                return HandlerOutcome.Fail($"close_scene failed: {e.Message}");
            }
        }
    }
}