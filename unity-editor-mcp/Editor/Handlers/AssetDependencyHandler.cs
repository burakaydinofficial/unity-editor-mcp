using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles Unity asset dependency analysis
    /// </summary>
    public static class AssetDependencyHandler
    {
        /// <summary>
        /// Handle asset dependency analysis operations
        /// </summary>
        public static HandlerOutcome HandleCommand(string action, JObject parameters)
        {
            try
            {
                switch (action.ToLower())
                {
                    case "get_dependencies":
                        var assetPath = parameters["assetPath"]?.ToString();
                        var recursive = parameters["recursive"]?.ToObject<bool>() ?? false;
                        return GetDependencies(assetPath, recursive, GetOffset(parameters), GetLimit(parameters));
                    case "get_dependents":
                        var dependentAssetPath = parameters["assetPath"]?.ToString();
                        return GetDependents(dependentAssetPath, GetOffset(parameters), GetLimit(parameters));
                    case "analyze_circular":
                        return AnalyzeCircularDependencies();
                    case "find_unused":
                        var includeBuiltIn = parameters["includeBuiltIn"]?.ToObject<bool>() ?? false;
                        return FindUnusedAssets(includeBuiltIn);
                    case "analyze_size_impact":
                        var sizeAssetPath = parameters["assetPath"]?.ToString();
                        return AnalyzeSizeImpact(sizeAssetPath);
                    case "validate_references":
                        return ValidateReferences();
                    default:
                        return HandlerOutcome.Fail($"Unknown action: {action}", "VALIDATION_ERROR");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDependencyHandler] Error handling {action}: {e.Message}");
                return HandlerOutcome.Fail(e.Message);
            }
        }

        // Paging helpers shared by get_dependencies / get_dependents (caps large dependency lists).
        private static int GetLimit(JObject p) { int l = p["limit"]?.ToObject<int?>() ?? 100; return l < 1 ? 1 : l; }
        private static int GetOffset(JObject p) { int o = p["offset"]?.ToObject<int?>() ?? 0; return o < 0 ? 0 : o; }

        /// <summary>
        /// Get dependencies of an asset
        /// </summary>
        private static HandlerOutcome GetDependencies(string assetPath, bool recursive, int offset, int limit)
        {
            try
            {
                if (string.IsNullOrEmpty(assetPath))
                {
                    return HandlerOutcome.Fail("Asset path not specified", "VALIDATION_ERROR");
                }
                { var g = PathSafety.Guard(assetPath, "assetPath"); if (g != null) return g; }

                if (!AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath))
                {
                    return HandlerOutcome.Fail($"Asset not found: {assetPath}", "NOT_FOUND");
                }

                var all = AssetDatabase.GetDependencies(assetPath, recursive);
                var depPaths = new List<string>();
                foreach (var d in all) if (d != assetPath) depPaths.Add(d);
                int total = depPaths.Count;
                // Direct-dependency set computed once (was an O(n^2) per-item call before).
                System.Collections.Generic.HashSet<string> direct = recursive
                    ? new System.Collections.Generic.HashSet<string>(AssetDatabase.GetDependencies(assetPath, false))
                    : null;
                var dependencyList = new List<object>();
                int maxDepth = 1;

                int idx = offset;
                for (; idx < total && dependencyList.Count < limit; idx++)
                {
                    var depPath = depPaths[idx];

                    var asset = AssetDatabase.LoadMainAssetAtPath(depPath);

                    // Calculate depth for recursive dependencies
                    int depth = 1;
                    bool isDirectDependency = true;
                    
                    if (recursive)
                    {
                        isDirectDependency = direct.Contains(depPath);
                        if (!isDirectDependency)
                        {
                            depth = 2;
                            maxDepth = Math.Max(maxDepth, depth);
                        }
                    }

                    dependencyList.Add(new
                    {
                        path = depPath,
                        type = asset != null ? asset.GetType().Name : "Unknown",
                        isDirectDependency = isDirectDependency,
                        depth = depth
                    });
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "get_dependencies",
                    assetPath = assetPath,
                    recursive = recursive,
                    dependencies = dependencyList,
                    count = dependencyList.Count,
                    total = total,
                    offset = offset,
                    limit = limit,
                    hasMore = idx < total,
                    maxDepth = maxDepth
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDependencyHandler] Error getting dependencies for '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to get dependencies: {e.Message}");
            }
        }

        /// <summary>
        /// Get assets that depend on the specified asset
        /// </summary>
        private static HandlerOutcome GetDependents(string assetPath, int offset, int limit)
        {
            try
            {
                if (string.IsNullOrEmpty(assetPath))
                {
                    return HandlerOutcome.Fail("Asset path not specified", "VALIDATION_ERROR");
                }
                { var g = PathSafety.Guard(assetPath, "assetPath"); if (g != null) return g; }

                if (!AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath))
                {
                    return HandlerOutcome.Fail($"Asset not found: {assetPath}", "NOT_FOUND");
                }

                var dependents = new List<object>();
                var allAssetGuids = AssetDatabase.FindAssets("");
                
                foreach (var guid in allAssetGuids)
                {
                    var otherAssetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (otherAssetPath == assetPath) continue;

                    var dependencies = AssetDatabase.GetDependencies(otherAssetPath, false);
                    if (dependencies.Contains(assetPath))
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(otherAssetPath);
                        if (asset != null)
                        {
                            // Try to determine usage context
                            string usage = "Unknown";
                            if (otherAssetPath.EndsWith(".prefab"))
                            {
                                if (assetPath.EndsWith(".mat"))
                                    usage = "Renderer.material";
                                else if (assetPath.EndsWith(".cs"))
                                    usage = "MonoBehaviour.script";
                                else if (assetPath.EndsWith(".png") || assetPath.EndsWith(".jpg"))
                                    usage = "UI.sprite or Material.texture";
                            }

                            dependents.Add(new
                            {
                                path = otherAssetPath,
                                type = asset.GetType().Name,
                                usage = usage
                            });
                        }
                    }
                }

                int total = dependents.Count;
                var page = new List<object>();
                for (int i = offset; i < total && page.Count < limit; i++) page.Add(dependents[i]);

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "get_dependents",
                    assetPath = assetPath,
                    dependents = page,
                    count = page.Count,
                    total = total,
                    offset = offset,
                    limit = limit,
                    hasMore = offset + page.Count < total
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDependencyHandler] Error getting dependents for '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to get dependents: {e.Message}");
            }
        }

        /// <summary>
        /// Analyze circular dependencies in the project
        /// </summary>
        private static HandlerOutcome AnalyzeCircularDependencies()
        {
            try
            {
                var circularDependencies = new List<object>();
                var allAssetGuids = AssetDatabase.FindAssets("t:Script");
                var visitedPaths = new HashSet<string>();
                var currentPath = new List<string>();

                // Simplified circular dependency detection for scripts
                foreach (var guid in allAssetGuids.Take(100)) // Limit to avoid performance issues
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (visitedPaths.Contains(assetPath)) continue;

                    if (HasCircularDependency(assetPath, currentPath, visitedPaths))
                    {
                        var cycle = new List<string>(currentPath);
                        cycle.Add(assetPath); // Complete the cycle

                        circularDependencies.Add(new
                        {
                            cycle = cycle,
                            length = cycle.Count,
                            severity = cycle.Count > 5 ? "error" : "warning"
                        });
                    }
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "analyze_circular",
                    circularDependencies = circularDependencies,
                    hasCircularDependencies = circularDependencies.Count > 0,
                    totalCycles = circularDependencies.Count
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDependencyHandler] Error analyzing circular dependencies: {e.Message}");
                return HandlerOutcome.Fail($"Failed to analyze circular dependencies: {e.Message}");
            }
        }

        /// <summary>
        /// Simple circular dependency check (simplified implementation)
        /// </summary>
        private static bool HasCircularDependency(string assetPath, List<string> currentPath, HashSet<string> visitedPaths)
        {
            if (currentPath.Contains(assetPath))
                return true;

            if (visitedPaths.Contains(assetPath))
                return false;

            visitedPaths.Add(assetPath);
            currentPath.Add(assetPath);

            var dependencies = AssetDatabase.GetDependencies(assetPath, false);
            foreach (var dep in dependencies)
            {
                if (dep != assetPath && dep.EndsWith(".cs"))
                {
                    if (HasCircularDependency(dep, currentPath, visitedPaths))
                        return true;
                }
            }

            currentPath.Remove(assetPath);
            return false;
        }

        /// <summary>
        /// Find unused assets in the project
        /// </summary>
        private static HandlerOutcome FindUnusedAssets(bool includeBuiltIn)
        {
            try
            {
                var unusedAssets = new List<object>();
                var allAssetGuids = AssetDatabase.FindAssets("");
                var usedAssets = new HashSet<string>();
                long totalSizeKB = 0;

                // First pass: collect all referenced assets
                foreach (var guid in allAssetGuids)
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!includeBuiltIn && assetPath.StartsWith("Packages/")) continue;

                    var dependencies = AssetDatabase.GetDependencies(assetPath, false);
                    foreach (var dep in dependencies)
                    {
                        usedAssets.Add(dep);
                    }
                }

                // Second pass: find assets that are not referenced
                foreach (var guid in allAssetGuids)
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!includeBuiltIn && assetPath.StartsWith("Packages/")) continue;
                    if (assetPath.EndsWith(".cs") || assetPath.EndsWith(".unity")) continue; // Skip scripts and scenes

                    if (!usedAssets.Contains(assetPath))
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                        if (asset != null)
                        {
                            var fileInfo = new FileInfo(Path.Combine(Application.dataPath, "..", assetPath));
                            var sizeKB = fileInfo.Exists ? (int)(fileInfo.Length / 1024) : 0;
                            totalSizeKB += sizeKB;

                            unusedAssets.Add(new
                            {
                                path = assetPath,
                                type = asset.GetType().Name,
                                size = sizeKB,
                                lastModified = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") : null
                            });
                        }
                    }
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "find_unused",
                    includeBuiltIn = includeBuiltIn,
                    unusedAssets = unusedAssets,
                    count = unusedAssets.Count,
                    totalSizeKB = totalSizeKB
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDependencyHandler] Error finding unused assets: {e.Message}");
                return HandlerOutcome.Fail($"Failed to find unused assets: {e.Message}");
            }
        }

        /// <summary>
        /// Analyze the size impact of an asset
        /// </summary>
        private static HandlerOutcome AnalyzeSizeImpact(string assetPath)
        {
            try
            {
                if (string.IsNullOrEmpty(assetPath))
                {
                    return HandlerOutcome.Fail("Asset path not specified", "VALIDATION_ERROR");
                }
                { var g = PathSafety.Guard(assetPath, "assetPath"); if (g != null) return g; }

                if (!AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath))
                {
                    return HandlerOutcome.Fail($"Asset not found: {assetPath}", "NOT_FOUND");
                }

                var fileInfo = new FileInfo(Path.Combine(Application.dataPath, "..", assetPath));
                var directSize = fileInfo.Exists ? (int)(fileInfo.Length / 1024) : 0;

                var dependencies = AssetDatabase.GetDependencies(assetPath, true);
                var totalSize = directSize;
                var dependencyCount = dependencies.Length - 1; // Exclude self
                var largestDependency = new { path = "", size = 0 };

                foreach (var dep in dependencies)
                {
                    if (dep == assetPath) continue;

                    var depFileInfo = new FileInfo(Path.Combine(Application.dataPath, "..", dep));
                    if (depFileInfo.Exists)
                    {
                        var depSize = (int)(depFileInfo.Length / 1024);
                        totalSize += depSize;

                        if (depSize > largestDependency.size)
                        {
                            largestDependency = new { path = dep, size = depSize };
                        }
                    }
                }

                var analysis = new Dictionary<string, object>
                {
                    ["directSize"] = directSize,
                    ["totalSize"] = totalSize,
                    ["dependencyCount"] = dependencyCount,
                    ["largestDependency"] = largestDependency,
                    ["buildImpact"] = new
                    {
                        estimatedBuildSize = (int)(totalSize * 0.8), // Assuming some compression
                        compressionRatio = 0.8
                    }
                };

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "analyze_size_impact",
                    assetPath = assetPath,
                    analysis = analysis
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDependencyHandler] Error analyzing size impact for '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to analyze size impact: {e.Message}");
            }
        }

        /// <summary>
        /// Validate asset references in the project
        /// </summary>
        private static HandlerOutcome ValidateReferences()
        {
            try
            {
                var startTime = EditorApplication.timeSinceStartup;
                var brokenReferences = new List<object>();
                var allAssetGuids = AssetDatabase.FindAssets("");
                var validReferences = 0;

                foreach (var guid in allAssetGuids.Take(150)) // Limit to avoid performance issues
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    var dependencies = AssetDatabase.GetDependencies(assetPath, false);

                    foreach (var dep in dependencies)
                    {
                        if (dep == assetPath) continue;

                        var depAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(dep);
                        if (depAsset == null)
                        {
                            // Determine reference type
                            string referenceType = "Unknown";
                            if (dep.EndsWith(".cs")) referenceType = "MonoScript";
                            else if (dep.EndsWith(".mat")) referenceType = "Material";
                            else if (dep.EndsWith(".png") || dep.EndsWith(".jpg")) referenceType = "Texture";

                            brokenReferences.Add(new
                            {
                                asset = assetPath,
                                missingReference = dep,
                                referenceType = referenceType
                            });
                        }
                        else
                        {
                            validReferences++;
                        }
                    }
                }

                var validationTime = EditorApplication.timeSinceStartup - startTime;

                var validation = new Dictionary<string, object>
                {
                    ["totalAssets"] = Math.Min(allAssetGuids.Length, 150),
                    ["validReferences"] = validReferences,
                    ["brokenReferences"] = brokenReferences,
                    ["missingReferences"] = brokenReferences.Count,
                    ["validationTime"] = Math.Round(validationTime, 2)
                };

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "validate_references",
                    validation = validation
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDependencyHandler] Error validating references: {e.Message}");
                return HandlerOutcome.Fail($"Failed to validate references: {e.Message}");
            }
        }
    }
}