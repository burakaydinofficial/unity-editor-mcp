using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles Unity tag management operations
    /// </summary>
    public static class TagManagementHandler
    {
        // Reserved Unity tags that cannot be removed
        private static readonly HashSet<string> RESERVED_TAGS = new HashSet<string>
        {
            "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController"
        };

        /// <summary>
        /// Handle tag management operations (add, remove, get)
        /// </summary>
        public static HandlerOutcome HandleCommand(string action, JObject parameters)
        {
            try
            {
                switch (action.ToLower())
                {
                    case "get":
                        return GetTags();
                    case "add":
                        var tagNameToAdd = parameters["tagName"]?.ToString();
                        return AddTag(tagNameToAdd);
                    case "remove":
                        var tagNameToRemove = parameters["tagName"]?.ToString();
                        return RemoveTag(tagNameToRemove);
                    default:
                        return HandlerOutcome.Fail($"Unknown action: {action}", "VALIDATION_ERROR");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TagManagementHandler] Error handling {action}: {e.Message}");
                return HandlerOutcome.Fail(e.Message);
            }
        }

        /// <summary>
        /// Get all available tags in the project
        /// </summary>
        public static HandlerOutcome GetTags()
        {
            try
            {
                var tags = InternalEditorUtility.tags.ToList();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "get",
                    tags = tags,
                    count = tags.Count
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[TagManagementHandler] Error getting tags: {e.Message}");
                return HandlerOutcome.Fail($"Failed to get tags: {e.Message}");
            }
        }

        /// <summary>
        /// Add a new tag to the project
        /// </summary>
        public static HandlerOutcome AddTag(string tagName)
        {
            try
            {
                if (string.IsNullOrEmpty(tagName))
                {
                    return HandlerOutcome.Fail("Tag name cannot be null or empty", "VALIDATION_ERROR");
                }

                // Validate tag name
                if (!IsValidTagName(tagName))
                {
                    return HandlerOutcome.Fail("Tag name contains invalid characters. Only letters, numbers, and underscores are allowed", "VALIDATION_ERROR");
                }

                // Check if tag already exists
                var currentTags = InternalEditorUtility.tags.ToList();
                if (currentTags.Contains(tagName))
                {
                    return HandlerOutcome.Fail($"Tag \"{tagName}\" already exists", "INVALID_STATE");
                }

                // Check for reserved tag names
                if (RESERVED_TAGS.Contains(tagName))
                {
                    return HandlerOutcome.Fail($"Tag \"{tagName}\" is reserved and cannot be added", "INVALID_STATE");
                }

                // Add the tag using SerializedObject approach
                var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
                var tagsProp = tagManager.FindProperty("tags");

                tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
                tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tagName;
                tagManager.ApplyModifiedProperties();

                // Force refresh of the tags
                AssetDatabase.Refresh();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "add",
                    tagName = tagName,
                    message = $"Tag \"{tagName}\" added successfully",
                    tagsCount = InternalEditorUtility.tags.Length
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[TagManagementHandler] Error adding tag '{tagName}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to add tag: {e.Message}");
            }
        }

        /// <summary>
        /// Remove an existing tag from the project
        /// </summary>
        public static HandlerOutcome RemoveTag(string tagName)
        {
            try
            {
                if (string.IsNullOrEmpty(tagName))
                {
                    return HandlerOutcome.Fail("Tag name cannot be null or empty", "VALIDATION_ERROR");
                }

                // Check for reserved tag names
                if (RESERVED_TAGS.Contains(tagName))
                {
                    return HandlerOutcome.Fail($"Cannot remove reserved tag \"{tagName}\"", "INVALID_STATE");
                }

                // Check if tag exists
                var currentTags = InternalEditorUtility.tags.ToList();
                if (!currentTags.Contains(tagName))
                {
                    return HandlerOutcome.Fail($"Tag \"{tagName}\" does not exist", "NOT_FOUND");
                }

                // Check if any GameObjects are using this tag
                var gameObjectsWithTag = GameObject.FindGameObjectsWithTag(tagName);
                if (gameObjectsWithTag.Length > 0)
                {
                    Debug.LogWarning($"[TagManagementHandler] Removing tag '{tagName}' while {gameObjectsWithTag.Length} GameObjects are still using it");
                }

                // Remove the tag using SerializedObject approach
                var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
                var tagsProp = tagManager.FindProperty("tags");

                for (int i = 0; i < tagsProp.arraySize; i++)
                {
                    if (tagsProp.GetArrayElementAtIndex(i).stringValue == tagName)
                    {
                        tagsProp.DeleteArrayElementAtIndex(i);
                        break;
                    }
                }
                tagManager.ApplyModifiedProperties();

                // Force refresh of the tags
                AssetDatabase.Refresh();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "remove",
                    tagName = tagName,
                    message = $"Tag \"{tagName}\" removed successfully",
                    tagsCount = InternalEditorUtility.tags.Length,
                    gameObjectsAffected = gameObjectsWithTag.Length
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[TagManagementHandler] Error removing tag '{tagName}': {e.Message}");
                return HandlerOutcome.Fail($"Failed to remove tag: {e.Message}");
            }
        }

        /// <summary>
        /// Validate if a tag name contains only valid characters
        /// </summary>
        private static bool IsValidTagName(string tagName)
        {
            if (string.IsNullOrEmpty(tagName))
                return false;

            // Unity tag names should only contain letters, numbers, and underscores
            foreach (char c in tagName)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Get tag usage statistics
        /// </summary>
        public static HandlerOutcome GetTagUsage()
        {
            try
            {
                var tags = InternalEditorUtility.tags.ToList();
                var tagUsage = new Dictionary<string, int>();

                foreach (var tag in tags)
                {
                    var gameObjectsWithTag = GameObject.FindGameObjectsWithTag(tag);
                    tagUsage[tag] = gameObjectsWithTag.Length;
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    tagUsage = tagUsage,
                    totalTags = tags.Count,
                    totalUsages = tagUsage.Values.Sum()
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[TagManagementHandler] Error getting tag usage: {e.Message}");
                return HandlerOutcome.Fail($"Failed to get tag usage: {e.Message}");
            }
        }
    }
}