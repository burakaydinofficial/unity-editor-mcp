using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles Unity asset import settings operations
    /// </summary>
    public static class AssetImportSettingsHandler
    {
        /// <summary>
        /// Handle asset import settings operations (get, modify, apply_preset, reimport)
        /// </summary>
        public static HandlerOutcome HandleCommand(string action, JObject parameters)
        {
            try
            {
                var assetPath = parameters["assetPath"]?.ToString();

                if (string.IsNullOrEmpty(assetPath))
                {
                    return HandlerOutcome.Fail("Asset path not specified", "VALIDATION_ERROR");
                }
                { var g = PathSafety.Guard(assetPath, "assetPath"); if (g != null) return g; } // H4: contain all actions' assetPath

                switch (action.ToLower())
                {
                    case "get":
                        return GetImportSettings(assetPath);
                    case "modify":
                        var settings = parameters["settings"] as JObject;
                        return ModifyImportSettings(assetPath, settings);
                    case "apply_preset":
                        var preset = parameters["preset"]?.ToString();
                        return ApplyPreset(assetPath, preset);
                    case "reimport":
                        return ReimportAsset(assetPath);
                    case "get_platform":
                        return GetPlatformSettings(assetPath, parameters["platform"]?.ToString());
                    case "set_platform":
                        return SetPlatformSettings(assetPath, parameters);
                    default:
                        return HandlerOutcome.Fail($"Unknown action: {action}", "VALIDATION_ERROR");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetImportSettingsHandler] Error handling {action}: {e.Message}");
                return HandlerOutcome.Fail(e.Message);
            }
        }

        /// <summary>
        /// Get import settings for an asset
        /// </summary>
        private static HandlerOutcome GetImportSettings(string assetPath)
        {
            try
            {
                var assetImporter = AssetImporter.GetAtPath(assetPath);
                if (assetImporter == null)
                {
                    return HandlerOutcome.Fail($"Asset not found: {assetPath}", "NOT_FOUND");
                }

                var settings = new Dictionary<string, object>();
                
                // Get asset type
                settings["assetType"] = assetImporter.GetType().Name.Replace("Importer", "");

                // Handle different importer types
                if (assetImporter is TextureImporter textureImporter)
                {
                    settings["textureType"] = textureImporter.textureType.ToString();
                    settings["filterMode"] = textureImporter.filterMode.ToString();
                    settings["wrapMode"] = textureImporter.wrapMode.ToString();
                    settings["maxTextureSize"] = textureImporter.maxTextureSize;
                    settings["compressionQuality"] = textureImporter.textureCompression == TextureImporterCompression.Compressed ? 50 : 100;
                    settings["generateMipMaps"] = textureImporter.mipmapEnabled;
                    settings["readable"] = textureImporter.isReadable;
                    settings["crunchedCompression"] = textureImporter.crunchedCompression;
                    settings["sRGBTexture"] = textureImporter.sRGBTexture;
                }
                else if (assetImporter is ModelImporter modelImporter)
                {
                    settings["assetType"] = "Model";
                    settings["scaleFactor"] = modelImporter.globalScale;
                    settings["useFileScale"] = modelImporter.useFileScale;
                    settings["importBlendShapes"] = modelImporter.importBlendShapes;
                    settings["importVisibility"] = modelImporter.importVisibility;
                    settings["importCameras"] = modelImporter.importCameras;
                    settings["importLights"] = modelImporter.importLights;
                    settings["generateColliders"] = modelImporter.addCollider;
                    settings["animationType"] = modelImporter.animationType.ToString();
                    settings["optimizeMesh"] = modelImporter.optimizeMeshPolygons;
                }
                else if (assetImporter is AudioImporter audioImporter)
                {
                    settings["assetType"] = "Audio";
                    settings["forceToMono"] = audioImporter.forceToMono;
                    settings["loadInBackground"] = audioImporter.loadInBackground;
                    settings["ambisonic"] = audioImporter.ambisonic;
                    
                    var sampleSettings = audioImporter.defaultSampleSettings;
                    settings["loadType"] = sampleSettings.loadType.ToString();
                    settings["compressionFormat"] = sampleSettings.compressionFormat.ToString();
                    settings["quality"] = sampleSettings.quality;
                    settings["sampleRateSetting"] = sampleSettings.sampleRateSetting.ToString();
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "get",
                    assetPath = assetPath,
                    settings = settings
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetImportSettingsHandler] Error getting import settings for '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to get import settings: {e.Message}");
            }
        }

        /// <summary>
        /// Modify import settings for an asset
        /// </summary>
        private static HandlerOutcome ModifyImportSettings(string assetPath, JObject newSettings)
        {
            try
            {
                if (newSettings == null)
                {
                    return HandlerOutcome.Fail("Settings not specified", "VALIDATION_ERROR");
                }

                var assetImporter = AssetImporter.GetAtPath(assetPath);
                if (assetImporter == null)
                {
                    return HandlerOutcome.Fail($"Asset not found: {assetPath}", "NOT_FOUND");
                }

                var previousSettings = new Dictionary<string, object>();
                var appliedSettings = new Dictionary<string, object>();

                // Only a hardcoded key allow-list for TextureImporter / ModelImporter is supported. Unrecognized keys
                // or importers are surfaced (skippedKeys, below) and a fully no-op apply fails — nothing is silently
                // reported as success (round-7 FR3).
                // Apply settings based on importer type
                if (assetImporter is TextureImporter textureImporter)
                {
                    foreach (var setting in newSettings)
                    {
                        var key = setting.Key;
                        var value = setting.Value;

                        switch (key)
                        {
                            case "maxTextureSize":
                                previousSettings[key] = textureImporter.maxTextureSize;
                                textureImporter.maxTextureSize = value.Value<int>();
                                appliedSettings[key] = value.Value<int>();
                                break;
                            case "compressionQuality":
                                previousSettings[key] = textureImporter.textureCompression == TextureImporterCompression.Compressed ? 50 : 100;
                                var quality = value.Value<int>();
                                textureImporter.textureCompression = quality < 100 ? TextureImporterCompression.Compressed : TextureImporterCompression.Uncompressed;
                                appliedSettings[key] = quality;
                                break;
                            case "textureType":
                                previousSettings[key] = textureImporter.textureType.ToString();
                                textureImporter.textureType = (TextureImporterType)Enum.Parse(typeof(TextureImporterType), value.ToString());
                                appliedSettings[key] = value.ToString();
                                break;
                            case "filterMode":
                                previousSettings[key] = textureImporter.filterMode.ToString();
                                textureImporter.filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), value.ToString());
                                appliedSettings[key] = value.ToString();
                                break;
                            case "generateMipMaps":
                                previousSettings[key] = textureImporter.mipmapEnabled;
                                textureImporter.mipmapEnabled = value.Value<bool>();
                                appliedSettings[key] = value.Value<bool>();
                                break;
                        }
                    }
                }
                else if (assetImporter is ModelImporter modelImporter)
                {
                    foreach (var setting in newSettings)
                    {
                        var key = setting.Key;
                        var value = setting.Value;

                        switch (key)
                        {
                            case "scaleFactor":
                                previousSettings[key] = modelImporter.globalScale;
                                modelImporter.globalScale = value.Value<float>();
                                appliedSettings[key] = value.Value<float>();
                                break;
                            case "animationType":
                                previousSettings[key] = modelImporter.animationType.ToString();
                                modelImporter.animationType = (ModelImporterAnimationType)Enum.Parse(typeof(ModelImporterAnimationType), value.ToString());
                                appliedSettings[key] = value.ToString();
                                break;
                            case "optimizeMesh":
                                previousSettings[key] = modelImporter.optimizeMeshPolygons;
                                modelImporter.optimizeMeshPolygons = value.Value<bool>();
                                appliedSettings[key] = value.Value<bool>();
                                break;
                        }
                    }
                }

                // Nothing recognized applied — refuse rather than report a no-op as success (round-7 FR3).
                if (appliedSettings.Count == 0)
                {
                    return HandlerOutcome.Fail(
                        $"No import settings applied to '{assetPath}': importer '{assetImporter.GetType().Name}' or the given keys are unsupported " +
                        "(supported: TextureImporter[maxTextureSize, compressionQuality, textureType, filterMode, generateMipMaps], " +
                        "ModelImporter[scaleFactor, animationType, optimizeMesh]).",
                        "UNSUPPORTED");
                }

                // Surface any requested keys that were NOT applied, so a partial apply isn't reported as a full one.
                var skippedKeys = new List<string>();
                foreach (var setting in newSettings)
                {
                    if (!appliedSettings.ContainsKey(setting.Key)) skippedKeys.Add(setting.Key);
                }

                // Apply the changes
                assetImporter.SaveAndReimport();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "modify",
                    assetPath = assetPath,
                    previousSettings = previousSettings,
                    newSettings = appliedSettings,
                    skippedKeys = skippedKeys,
                    message = skippedKeys.Count == 0
                        ? $"Import settings modified for: {assetPath}"
                        : $"Import settings partially modified for {assetPath}: applied {appliedSettings.Count}, skipped {skippedKeys.Count} unsupported key(s): {string.Join(", ", skippedKeys)}"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetImportSettingsHandler] Error modifying import settings for '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to modify import settings: {e.Message}");
            }
        }

        /// <summary>
        /// Apply a preset to an asset
        /// </summary>
        private static HandlerOutcome ApplyPreset(string assetPath, string preset)
        {
            try
            {
                if (string.IsNullOrEmpty(preset))
                {
                    return HandlerOutcome.Fail("Preset not specified", "VALIDATION_ERROR");
                }

                var assetImporter = AssetImporter.GetAtPath(assetPath);
                if (assetImporter == null)
                {
                    return HandlerOutcome.Fail($"Asset not found: {assetPath}", "NOT_FOUND");
                }

                var appliedSettings = new Dictionary<string, object>();

                // Apply preset based on type
                if (assetImporter is TextureImporter textureImporter)
                {
                    switch (preset)
                    {
                        case "UI_Sprite":
                            textureImporter.textureType = TextureImporterType.Sprite;
                            textureImporter.filterMode = FilterMode.Bilinear;
                            textureImporter.maxTextureSize = 2048;
                            textureImporter.mipmapEnabled = false;

                            appliedSettings["textureType"] = "Sprite";
                            appliedSettings["filterMode"] = "Bilinear";
                            appliedSettings["maxTextureSize"] = 2048;
                            appliedSettings["generateMipMaps"] = false;
                            break;

                        case "3D_Texture":
                            textureImporter.textureType = TextureImporterType.Default;
                            textureImporter.filterMode = FilterMode.Trilinear;
                            textureImporter.mipmapEnabled = true;
                            textureImporter.anisoLevel = 4;

                            appliedSettings["textureType"] = "Default";
                            appliedSettings["filterMode"] = "Trilinear";
                            appliedSettings["generateMipMaps"] = true;
                            appliedSettings["anisoLevel"] = 4;
                            break;

                        case "Icon":
                            textureImporter.textureType = TextureImporterType.Sprite;
                            textureImporter.filterMode = FilterMode.Point;
                            textureImporter.maxTextureSize = 256;
                            textureImporter.mipmapEnabled = false;
                            textureImporter.textureCompression = TextureImporterCompression.Uncompressed;

                            appliedSettings["textureType"] = "Sprite";
                            appliedSettings["filterMode"] = "Point";
                            appliedSettings["maxTextureSize"] = 256;
                            appliedSettings["generateMipMaps"] = false;
                            appliedSettings["compression"] = "None";
                            break;

                        default:
                            return HandlerOutcome.Fail($"Unknown preset: {preset}", "VALIDATION_ERROR");
                    }
                }
                else if (assetImporter is ModelImporter modelImporter)
                {
                    switch (preset)
                    {
                        case "Character":
                            modelImporter.animationType = ModelImporterAnimationType.Human;
                            modelImporter.optimizeMeshPolygons = true;
                            modelImporter.importBlendShapes = true;

                            appliedSettings["animationType"] = "Human";
                            appliedSettings["optimizeMesh"] = true;
                            appliedSettings["importBlendShapes"] = true;
                            break;

                        case "Static_Prop":
                            modelImporter.animationType = ModelImporterAnimationType.None;
                            modelImporter.optimizeMeshPolygons = true;
                            modelImporter.addCollider = false;
                            modelImporter.generateSecondaryUV = true;

                            appliedSettings["animationType"] = "None";
                            appliedSettings["optimizeMesh"] = true;
                            appliedSettings["generateColliders"] = false;
                            appliedSettings["generateLightmapUVs"] = true;
                            break;

                        default:
                            return HandlerOutcome.Fail($"Unknown preset: {preset}", "VALIDATION_ERROR");
                    }
                }
                else
                {
                    return HandlerOutcome.Fail($"No presets available for asset type: {assetImporter.GetType().Name}", "INVALID_STATE");
                }

                // Apply the changes
                assetImporter.SaveAndReimport();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "apply_preset",
                    assetPath = assetPath,
                    preset = preset,
                    appliedSettings = appliedSettings,
                    message = $"Preset \"{preset}\" applied to: {assetPath}"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetImportSettingsHandler] Error applying preset '{preset}' to '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to apply preset: {e.Message}");
            }
        }

        /// <summary>
        /// Reimport an asset
        /// </summary>
        private static HandlerOutcome ReimportAsset(string assetPath)
        {
            try
            {
                if (!AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath))
                {
                    return HandlerOutcome.Fail($"Asset not found: {assetPath}", "NOT_FOUND");
                }

                var startTime = EditorApplication.timeSinceStartup;

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

                var duration = EditorApplication.timeSinceStartup - startTime;

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "reimport",
                    assetPath = assetPath,
                    message = $"Asset reimported: {assetPath}",
                    duration = Math.Round(duration, 3)
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetImportSettingsHandler] Error reimporting '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to reimport asset: {e.Message}");
            }
        }

        // ===== Per-platform texture overrides (E-tail) =====
        private static readonly string[] CommonTexturePlatforms = { "Standalone", "Android", "iPhone", "WebGL" };

        // Unity's texture-platform names differ from common usage; map the obvious aliases.
        private static string NormalizePlatform(string platform)
        {
            if (string.IsNullOrEmpty(platform)) return platform;
            if (platform.Equals("iOS", StringComparison.OrdinalIgnoreCase)) return "iPhone";
            if (platform.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || platform.Equals("OSX", StringComparison.OrdinalIgnoreCase)
                || platform.Equals("macOS", StringComparison.OrdinalIgnoreCase))
                return "Standalone";
            return platform;
        }

        private static object DescribePlatform(TextureImporterPlatformSettings s)
        {
            return new
            {
                platform = s.name,
                overridden = s.overridden,
                maxTextureSize = s.maxTextureSize,
                format = s.format.ToString(),
                textureCompression = s.textureCompression.ToString(),
                compressionQuality = s.compressionQuality,
                crunchedCompression = s.crunchedCompression
            };
        }

        private static HandlerOutcome GetPlatformSettings(string assetPath, string platform)
        {
            try
            {
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null)
                    return HandlerOutcome.Fail($"Platform overrides apply only to textures (not a TextureImporter): {assetPath}", "INVALID_STATE");

                if (!string.IsNullOrEmpty(platform))
                {
                    var s = importer.GetPlatformTextureSettings(NormalizePlatform(platform));
                    return HandlerOutcome.Ok(new { success = true, action = "get_platform", assetPath, platform = DescribePlatform(s) });
                }

                var list = new List<object>();
                foreach (var name in CommonTexturePlatforms)
                    list.Add(DescribePlatform(importer.GetPlatformTextureSettings(name)));
                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "get_platform",
                    assetPath,
                    defaultPlatform = DescribePlatform(importer.GetDefaultPlatformTextureSettings()),
                    platforms = list
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetImportSettingsHandler] Error in get_platform for '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to get platform settings: {e.Message}");
            }
        }

        private static HandlerOutcome SetPlatformSettings(string assetPath, JObject parameters)
        {
            try
            {
                var platform = parameters["platform"]?.ToString();
                if (string.IsNullOrEmpty(platform))
                    return HandlerOutcome.Fail("platform is required for set_platform", "VALIDATION_ERROR");

                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null)
                    return HandlerOutcome.Fail($"Platform overrides apply only to textures (not a TextureImporter): {assetPath}", "INVALID_STATE");

                var resolved = NormalizePlatform(platform);
                var s = importer.GetPlatformTextureSettings(resolved);
                var applied = new Dictionary<string, object>();

                s.overridden = parameters["overridden"]?.ToObject<bool>() ?? true;
                applied["overridden"] = s.overridden;

                if (parameters["maxTextureSize"] != null)
                {
                    s.maxTextureSize = parameters["maxTextureSize"].Value<int>();
                    applied["maxTextureSize"] = s.maxTextureSize;
                }
                if (parameters["compressionQuality"] != null)
                {
                    s.compressionQuality = parameters["compressionQuality"].Value<int>();
                    applied["compressionQuality"] = s.compressionQuality;
                }
                if (parameters["textureCompression"] != null)
                {
                    var tc = parameters["textureCompression"].ToString();
                    if (!Enum.TryParse(tc, true, out TextureImporterCompression parsedTc))
                        return HandlerOutcome.Fail($"Unknown TextureImporterCompression: {tc}", "VALIDATION_ERROR");
                    s.textureCompression = parsedTc;
                    applied["textureCompression"] = parsedTc.ToString();
                }
                if (parameters["format"] != null)
                {
                    var fmt = parameters["format"].ToString();
                    if (!Enum.TryParse(fmt, true, out TextureImporterFormat parsedFmt))
                        return HandlerOutcome.Fail($"Unknown TextureImporterFormat: {fmt}", "VALIDATION_ERROR");
                    s.format = parsedFmt;
                    applied["format"] = parsedFmt.ToString();
                }

                importer.SetPlatformTextureSettings(s);
                importer.SaveAndReimport();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "set_platform",
                    assetPath,
                    platform = resolved,
                    applied,
                    settings = DescribePlatform(importer.GetPlatformTextureSettings(resolved)),
                    message = $"Platform override set for {resolved} on {assetPath}"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetImportSettingsHandler] Error in set_platform for '{assetPath}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to set platform settings: {e.Message}");
            }
        }
    }
}