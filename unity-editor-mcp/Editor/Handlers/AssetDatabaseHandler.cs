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
    /// Handles Unity Asset Database operations
    /// </summary>
    public static class AssetDatabaseHandler
    {
        /// <summary>
        /// Handle asset database operations (find_assets, get_asset_info, create_folder, etc.)
        /// </summary>
        public static HandlerOutcome HandleCommand(string action, JObject parameters)
        {
            try
            {
                switch (action.ToLower())
                {
                    case "find_assets":
                        var filter = parameters["filter"]?.ToString();
                        var searchInFolders = parameters["searchInFolders"]?.ToObject<string[]>();
                        foreach (var sf in searchInFolders ?? System.Array.Empty<string>())
                            { var g = PathSafety.Guard(sf, "searchInFolders"); if (g != null) return g; } // H4
                        return FindAssets(filter, searchInFolders);
                    case "get_asset_info":
                        var assetPath = parameters["assetPath"]?.ToString();
                        { var g = PathSafety.Guard(assetPath, "assetPath"); if (g != null) return g; } // H4
                        return GetAssetInfo(assetPath);
                    case "create_folder":
                        var folderPath = parameters["folderPath"]?.ToString();
                        { var g = PathSafety.Guard(folderPath, "folderPath"); if (g != null) return g; } // H4
                        return CreateFolder(folderPath);
                    case "delete_asset":
                        var deleteAssetPath = parameters["assetPath"]?.ToString();
                        var confirmDelete = parameters["confirm"]?.ToObject<bool>() ?? false;
                        { var g = PathSafety.Guard(deleteAssetPath, "assetPath"); if (g != null) return g; } // H4
                        return DeleteAsset(deleteAssetPath, confirmDelete);
                    case "move_asset":
                        var fromPath = parameters["fromPath"]?.ToString();
                        var toPath = parameters["toPath"]?.ToString();
                        { var g = PathSafety.Guard(fromPath, "fromPath") ?? PathSafety.Guard(toPath, "toPath"); if (g != null) return g; } // H4
                        return MoveAsset(fromPath, toPath);
                    case "copy_asset":
                        var copyFromPath = parameters["fromPath"]?.ToString();
                        var copyToPath = parameters["toPath"]?.ToString();
                        { var g = PathSafety.Guard(copyFromPath, "fromPath") ?? PathSafety.Guard(copyToPath, "toPath"); if (g != null) return g; } // H4
                        return CopyAsset(copyFromPath, copyToPath);
                    case "refresh":
                        return RefreshAssetDatabase();
                    case "save":
                        return SaveAssetDatabase();
                    default:
                        return HandlerOutcome.Fail($"Unknown action: {action}", "VALIDATION_ERROR");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDatabaseHandler] Error handling {action}: {e.Message}");
                return HandlerOutcome.Fail(e.Message);
            }
        }

        /// <summary>
        /// Find assets using AssetDatabase search filters
        /// </summary>
        private static HandlerOutcome FindAssets(string filter, string[] searchInFolders)
        {
            try
            {
                if (string.IsNullOrEmpty(filter))
                {
                    return HandlerOutcome.Fail("Filter not specified", "VALIDATION_ERROR");
                }

                var guids = AssetDatabase.FindAssets(filter, searchInFolders);
                var assets = new List<object>();

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);

                    if (asset != null)
                    {
                        var fileInfo = new FileInfo(Path.Combine(Application.dataPath, "..", path));

                        assets.Add(new
                        {
                            path = path,
                            name = asset.name,
                            type = asset.GetType().Name,
                            guid = guid,
                            size = fileInfo.Exists ? (int)(fileInfo.Length / 1024) : 0 // Size in KB
                        });
                    }
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "find_assets",
                    filter = filter,
                    searchInFolders = searchInFolders ?? new string[0],
                    assets = assets,
                    count = assets.Count
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDatabaseHandler] Error finding assets with filter '{filter}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to find assets: {e.Message}");
            }
        }

        /// <summary>
        /// Get detailed information about an asset
        /// </summary>
        private static HandlerOutcome GetAssetInfo(string assetPath)
        {
            try
            {
                if (string.IsNullOrEmpty(assetPath))
                {
                    return HandlerOutcome.Fail("Asset path not specified", "VALIDATION_ERROR");
                }

                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset == null)
                {
                    return HandlerOutcome.Fail($"Asset not found: {assetPath}", "NOT_FOUND");
                }

                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var fileInfo = new FileInfo(Path.Combine(Application.dataPath, "..", assetPath));
                var importer = AssetImporter.GetAtPath(assetPath);

                // Get dependencies
                var dependencies = AssetDatabase.GetDependencies(assetPath, false).Where(dep => dep != assetPath).ToArray();

                // Get import settings based on asset type
                var importSettings = new Dictionary<string, object>();
                if (importer is TextureImporter textureImporter)
                {
                    importSettings["textureType"] = textureImporter.textureType.ToString();
                    importSettings["maxTextureSize"] = textureImporter.maxTextureSize;
                    importSettings["filterMode"] = textureImporter.filterMode.ToString();
                }
                else if (importer is ModelImporter modelImporter)
                {
                    importSettings["scaleFactor"] = modelImporter.globalScale;
                    importSettings["animationType"] = modelImporter.animationType.ToString();
                }
                else if (importer is AudioImporter audioImporter)
                {
                    var settings = audioImporter.defaultSampleSettings;
                    importSettings["loadType"] = settings.loadType.ToString();
                    importSettings["compressionFormat"] = settings.compressionFormat.ToString();
                }

                var info = new Dictionary<string, object>
                {
                    ["name"] = asset.name,
                    ["type"] = asset.GetType().Name,
                    ["guid"] = guid,
                    ["size"] = fileInfo.Exists ? (int)(fileInfo.Length / 1024) : 0, // Size in KB
                    ["lastModified"] = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") : null,
                    ["importSettings"] = importSettings,
                    ["dependencies"] = dependencies,
                    ["isValid"] = asset != null
                };

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "get_asset_info",
                    assetPath = assetPath,
                    info = info
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDatabaseHandler] Error getting asset info for '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to get asset info: {e.Message}");
            }
        }

        /// <summary>
        /// Create a new folder in the Asset Database
        /// </summary>
        private static HandlerOutcome CreateFolder(string folderPath)
        {
            try
            {
                if (string.IsNullOrEmpty(folderPath))
                {
                    return HandlerOutcome.Fail("Folder path not specified", "VALIDATION_ERROR");
                }

                if (AssetDatabase.IsValidFolder(folderPath))
                {
                    return HandlerOutcome.Fail($"Folder already exists: {folderPath}", "INVALID_STATE");
                }

                // Extract parent folder and folder name
                var parentPath = Path.GetDirectoryName(folderPath).Replace('\\', '/');
                var folderName = Path.GetFileName(folderPath);

                if (!AssetDatabase.IsValidFolder(parentPath))
                {
                    return HandlerOutcome.Fail($"Parent folder does not exist: {parentPath}", "NOT_FOUND");
                }

                var guid = AssetDatabase.CreateFolder(parentPath, folderName);

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "create_folder",
                    folderPath = folderPath,
                    guid = guid,
                    message = $"Folder created: {folderPath}"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDatabaseHandler] Error creating folder '{folderPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to create folder: {e.Message}");
            }
        }

        /// <summary>
        /// Delete an asset from the Asset Database
        /// </summary>
        private static HandlerOutcome DeleteAsset(string assetPath, bool confirm)
        {
            try
            {
                if (string.IsNullOrEmpty(assetPath))
                {
                    return HandlerOutcome.Fail("Asset path not specified", "VALIDATION_ERROR");
                }

                if (!AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath))
                {
                    return HandlerOutcome.Fail($"Asset not found: {assetPath}", "NOT_FOUND");
                }

                var dependents = FindDependents(assetPath);
                // Destructive: dry-run by default — require an explicit confirm:true and surface what still
                // references this asset, so an agent never blind-deletes something in use (H3).
                if (!confirm)
                {
                    // Consistent with the central H3 gate: refusal is an ERROR (CONFIRMATION_REQUIRED), not a
                    // success envelope. Dependents go in details so the agent can see what references the asset.
                    return HandlerOutcome.Fail(
                        dependents.Count > 0
                            ? $"'{assetPath}' is referenced by {dependents.Count} asset(s). Re-call with confirm:true to delete."
                            : $"Re-call with confirm:true to delete '{assetPath}'.",
                        "CONFIRMATION_REQUIRED",
                        details: new { wouldDelete = assetPath, dependents = dependents, dependentCount = dependents.Count });
                }

                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    return HandlerOutcome.Fail($"Failed to delete asset: {assetPath}", "INVALID_STATE");
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "delete_asset",
                    assetPath = assetPath,
                    deleted = assetPath,
                    hadDependents = dependents.Count,
                    message = $"Asset deleted: {assetPath}"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDatabaseHandler] Error deleting asset '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to delete asset: {e.Message}");
            }
        }

        // Assets that directly reference assetPath (reverse dependency scan). O(N) over project assets — a
        // pre-delete safety check, not a hot path. internal so the asset-creation overwrite paths reuse it.
        internal static List<string> FindDependents(string assetPath)
        {
            var result = new List<string>();
            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (p == assetPath || !p.StartsWith("Assets/") || AssetDatabase.IsValidFolder(p)) continue;
                var deps = AssetDatabase.GetDependencies(p, false);
                if (System.Array.IndexOf(deps, assetPath) >= 0) result.Add(p);
            }
            return result;
        }

        /// <summary>
        /// Move an asset to a new location
        /// </summary>
        private static HandlerOutcome MoveAsset(string fromPath, string toPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath))
                {
                    return HandlerOutcome.Fail("Source and destination paths must be specified", "VALIDATION_ERROR");
                }

                if (!AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(fromPath))
                {
                    return HandlerOutcome.Fail($"Source asset not found: {fromPath}", "NOT_FOUND");
                }

                var errorMessage = AssetDatabase.MoveAsset(fromPath, toPath);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return HandlerOutcome.Fail($"Failed to move asset: {errorMessage}", "INVALID_STATE");
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "move_asset",
                    fromPath = fromPath,
                    toPath = toPath,
                    message = $"Asset moved from {fromPath} to {toPath}"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDatabaseHandler] Error moving asset from '{fromPath}' to '{toPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to move asset: {e.Message}");
            }
        }

        /// <summary>
        /// Copy an asset to a new location
        /// </summary>
        private static HandlerOutcome CopyAsset(string fromPath, string toPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath))
                {
                    return HandlerOutcome.Fail("Source and destination paths must be specified", "VALIDATION_ERROR");
                }

                if (!AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(fromPath))
                {
                    return HandlerOutcome.Fail($"Source asset not found: {fromPath}", "NOT_FOUND");
                }

                if (!AssetDatabase.CopyAsset(fromPath, toPath))
                {
                    return HandlerOutcome.Fail($"Failed to copy asset from {fromPath} to {toPath}", "INVALID_STATE");
                }

                var newGuid = AssetDatabase.AssetPathToGUID(toPath);

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "copy_asset",
                    fromPath = fromPath,
                    toPath = toPath,
                    newGuid = newGuid,
                    message = $"Asset copied from {fromPath} to {toPath}"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDatabaseHandler] Error copying asset from '{fromPath}' to '{toPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to copy asset: {e.Message}");
            }
        }

        /// <summary>
        /// Refresh the Asset Database
        /// </summary>
        private static HandlerOutcome RefreshAssetDatabase()
        {
            try
            {
                var startTime = EditorApplication.timeSinceStartup;

                AssetDatabase.Refresh();

                var duration = EditorApplication.timeSinceStartup - startTime;

                // Count assets in project
                var allAssetGuids = AssetDatabase.FindAssets("");
                var assetsFound = allAssetGuids.Length;

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "refresh",
                    message = "Asset database refreshed",
                    assetsFound = assetsFound,
                    duration = Math.Round(duration, 2)
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDatabaseHandler] Error refreshing asset database: {e.Message}");
                return HandlerOutcome.Fail($"Failed to refresh asset database: {e.Message}");
            }
        }

        /// <summary>
        /// Save pending changes to the Asset Database
        /// </summary>
        private static HandlerOutcome SaveAssetDatabase()
        {
            try
            {
                // Get count of modified assets (this is an approximation)
                var allAssetGuids = AssetDatabase.FindAssets("");
                var modifiedCount = 0;

                // Count recently modified assets (modified in last hour as an example)
                var oneHourAgo = DateTime.UtcNow.AddHours(-1);
                foreach (var guid in allAssetGuids.Take(100)) // Limit to avoid performance issues
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var fileInfo = new FileInfo(Path.Combine(Application.dataPath, "..", path));
                    if (fileInfo.Exists && fileInfo.LastWriteTimeUtc > oneHourAgo)
                    {
                        modifiedCount++;
                    }
                }

                AssetDatabase.SaveAssets();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "save",
                    message = "Asset database saved",
                    assetsModified = modifiedCount
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetDatabaseHandler] Error saving asset database: {e.Message}");
                return HandlerOutcome.Fail($"Failed to save asset database: {e.Message}");
            }
        }
    }
}
