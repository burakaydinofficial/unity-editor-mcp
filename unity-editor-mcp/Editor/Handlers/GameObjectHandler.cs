using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles GameObject-related operations
    /// </summary>
    public static class GameObjectHandler
    {
        /// <summary>
        /// Creates a new GameObject based on parameters
        /// </summary>
        public static HandlerOutcome CreateGameObject(JObject parameters)
        {
            try
            {
                // Parse parameters
                string name = parameters["name"]?.ToString() ?? "GameObject";
                string primitiveType = parameters["primitiveType"]?.ToString();

                // Parse transform
                var position = ParseVector3(parameters["position"]) ?? Vector3.zero;
                var rotation = ParseVector3(parameters["rotation"]) ?? Vector3.zero;
                var scale = ParseVector3(parameters["scale"]) ?? Vector3.one;

                // Parse parent
                string parentPath = parameters["parentPath"]?.ToString();
                GameObject parent = null;
                if (!string.IsNullOrEmpty(parentPath))
                {
                    parent = GameObject.Find(parentPath);
                    if (parent == null)
                    {
                        return HandlerOutcome.Fail($"Parent GameObject not found: {parentPath}", "NOT_FOUND");
                    }
                }

                // Create GameObject
                GameObject newObject;
                if (!string.IsNullOrEmpty(primitiveType))
                {
                    newObject = CreatePrimitive(primitiveType);
                    if (newObject == null)
                    {
                        return HandlerOutcome.Fail($"Unknown primitive type: {primitiveType}", "VALIDATION_ERROR");
                    }
                }
                else
                {
                    newObject = new GameObject();
                }

                // Set properties
                newObject.name = name;
                newObject.transform.position = position;
                newObject.transform.rotation = Quaternion.Euler(rotation);
                newObject.transform.localScale = scale;

                // Set parent
                if (parent != null)
                {
                    newObject.transform.SetParent(parent.transform, true);
                }

                // Set tag
                string tag = parameters["tag"]?.ToString();
                if (!string.IsNullOrEmpty(tag))
                {
                    try
                    {
                        newObject.tag = tag;
                    }
                    catch (Exception)
                    {
                        Debug.LogWarning($"Invalid tag: {tag}");
                    }
                }

                // Set layer
                int? layer = parameters["layer"]?.ToObject<int>();
                if (layer.HasValue && layer.Value >= 0 && layer.Value < 32)
                {
                    newObject.layer = layer.Value;
                }

                // Register undo
                Undo.RegisterCreatedObjectUndo(newObject, $"Create {name}");

                // Select the new object
                Selection.activeGameObject = newObject;

                // Return info about created object
                return HandlerOutcome.Ok(new
                {
                    id = newObject.GetInstanceID(),
                    name = newObject.name,
                    path = GetGameObjectPath(newObject),
                    position = new { x = position.x, y = position.y, z = position.z },
                    rotation = new { x = rotation.x, y = rotation.y, z = rotation.z },
                    scale = new { x = scale.x, y = scale.y, z = scale.z },
                    tag = newObject.tag,
                    layer = newObject.layer,
                    isActive = newObject.activeSelf
                });
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to create GameObject: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Creates a primitive GameObject
        /// </summary>
        private static GameObject CreatePrimitive(string type)
        {
            switch (type.ToLower())
            {
                case "cube":
                    return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "sphere":
                    return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "cylinder":
                    return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "capsule":
                    return GameObject.CreatePrimitive(PrimitiveType.Capsule);
                case "plane":
                    return GameObject.CreatePrimitive(PrimitiveType.Plane);
                case "quad":
                    return GameObject.CreatePrimitive(PrimitiveType.Quad);
                default:
                    return null;
            }
        }
        
        /// <summary>
        /// Parses a Vector3 from JToken
        /// </summary>
        private static Vector3? ParseVector3(JToken token)
        {
            if (token == null) return null;
            
            try
            {
                if (token is JObject obj)
                {
                    float x = obj["x"]?.ToObject<float>() ?? 0;
                    float y = obj["y"]?.ToObject<float>() ?? 0;
                    float z = obj["z"]?.ToObject<float>() ?? 0;
                    return new Vector3(x, y, z);
                }
            }
            catch
            {
                // Invalid format
            }
            
            return null;
        }
        
        /// <summary>
        /// Gets the full hierarchy path of a GameObject
        /// </summary>
        public static string GetGameObjectPath(GameObject obj)
        {
            if (obj == null) return "";
            
            string path = obj.name;
            Transform parent = obj.transform.parent;
            
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            
            return "/" + path;
        }
        
        /// <summary>
        /// Modifies an existing GameObject
        /// </summary>
        public static HandlerOutcome ModifyGameObject(JObject parameters)
        {
            try
            {
                string path = parameters["path"]?.ToString();
                if (string.IsNullOrEmpty(path))
                {
                    return HandlerOutcome.Fail("GameObject path is required", "VALIDATION_ERROR");
                }

                // Find the GameObject
                GameObject obj = GameObject.Find(path);
                if (obj == null)
                {
                    return HandlerOutcome.Fail($"GameObject not found: {path}", "NOT_FOUND");
                }
                
                // Store original values for undo
                var originalName = obj.name;
                var originalPosition = obj.transform.position;
                var originalRotation = obj.transform.rotation;
                var originalScale = obj.transform.localScale;
                var originalActive = obj.activeSelf;

                // Register undo BEFORE mutating so Unity snapshots the ORIGINAL state.
                // (RecordObject called after the change records the post-change state,
                // which makes Ctrl+Z a no-op — the bug this fixes.)
                Undo.RecordObject(obj, "Modify GameObject");
                Undo.RecordObject(obj.transform, "Modify GameObject Transform");

                // Apply modifications
                bool modified = false;
                
                // Name
                string newName = parameters["name"]?.ToString();
                if (!string.IsNullOrEmpty(newName) && newName != obj.name)
                {
                    obj.name = newName;
                    modified = true;
                }
                
                // Transform. F3: explicit space — world (default) uses transform.position/.rotation; local uses
                // localPosition/localEulerAngles. (scale is always localScale — there is no world scale.)
                bool local = string.Equals(parameters["space"]?.ToString(), "local", StringComparison.OrdinalIgnoreCase);
                var position = ParseVector3(parameters["position"]);
                if (position.HasValue)
                {
                    if (local) obj.transform.localPosition = position.Value;
                    else obj.transform.position = position.Value;
                    modified = true;
                }

                var rotation = ParseVector3(parameters["rotation"]);
                if (rotation.HasValue)
                {
                    if (local) obj.transform.localEulerAngles = rotation.Value;
                    else obj.transform.rotation = Quaternion.Euler(rotation.Value);
                    modified = true;
                }
                
                var scale = ParseVector3(parameters["scale"]);
                if (scale.HasValue)
                {
                    obj.transform.localScale = scale.Value;
                    modified = true;
                }
                
                // Active state
                bool? active = parameters["active"]?.ToObject<bool>();
                if (active.HasValue && active.Value != obj.activeSelf)
                {
                    obj.SetActive(active.Value);
                    modified = true;
                }
                
                // Tag
                string tag = parameters["tag"]?.ToString();
                if (!string.IsNullOrEmpty(tag) && tag != obj.tag)
                {
                    try
                    {
                        obj.tag = tag;
                        modified = true;
                    }
                    catch (Exception)
                    {
                        Debug.LogWarning($"Invalid tag: {tag}");
                    }
                }
                
                // Layer
                int? layer = parameters["layer"]?.ToObject<int>();
                if (layer.HasValue && layer.Value >= 0 && layer.Value < 32 && layer.Value != obj.layer)
                {
                    obj.layer = layer.Value;
                    modified = true;
                }
                
                // Parent
                string parentPath = parameters["parentPath"]?.ToString();
                if (parameters.ContainsKey("parentPath")) // Allow null to unparent
                {
                    GameObject newParent = null;
                    if (!string.IsNullOrEmpty(parentPath))
                    {
                        newParent = GameObject.Find(parentPath);
                        if (newParent == null)
                        {
                            return HandlerOutcome.Fail($"Parent GameObject not found: {parentPath}", "NOT_FOUND");
                        }
                    }
                    
                    if (obj.transform.parent != (newParent ? newParent.transform : null))
                    {
                        obj.transform.SetParent(newParent ? newParent.transform : null, true);
                        modified = true;
                    }
                }

                // A reparent above uses worldPositionStays=true (preserves world coords, recomputes local). So if
                // the caller asked for a LOCAL-space transform AND changed the parent, re-apply it now relative to
                // the NEW parent — otherwise the local values would be computed against the old parent.
                if (local && parameters.ContainsKey("parentPath"))
                {
                    if (position.HasValue) obj.transform.localPosition = position.Value;
                    if (rotation.HasValue) obj.transform.localEulerAngles = rotation.Value;
                }
                
                if (modified)
                {
                    // Mark as dirty for saving (undo was already recorded above, before
                    // the mutations).
                    EditorUtility.SetDirty(obj);
                }
                
                // Return updated info
                return HandlerOutcome.Ok(new
                {
                    id = obj.GetInstanceID(),
                    name = obj.name,
                    path = GetGameObjectPath(obj),
                    position = new { x = obj.transform.position.x, y = obj.transform.position.y, z = obj.transform.position.z },
                    localPosition = new { x = obj.transform.localPosition.x, y = obj.transform.localPosition.y, z = obj.transform.localPosition.z },
                    rotation = new { x = obj.transform.rotation.eulerAngles.x, y = obj.transform.rotation.eulerAngles.y, z = obj.transform.rotation.eulerAngles.z },
                    localRotation = new { x = obj.transform.localEulerAngles.x, y = obj.transform.localEulerAngles.y, z = obj.transform.localEulerAngles.z },
                    scale = new { x = obj.transform.localScale.x, y = obj.transform.localScale.y, z = obj.transform.localScale.z },
                    tag = obj.tag,
                    layer = obj.layer,
                    isActive = obj.activeSelf,
                    modified = modified
                });
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to modify GameObject: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Finds GameObjects based on search criteria
        /// </summary>
        public static HandlerOutcome FindGameObjects(JObject parameters)
        {
            try
            {
                string name = parameters["name"]?.ToString();
                string tag = parameters["tag"]?.ToString();
                int? layer = parameters["layer"]?.ToObject<int>();
                bool exactMatch = parameters["exactMatch"]?.ToObject<bool>() ?? true;
                int limit = parameters["limit"]?.ToObject<int>() ?? 200;
                if (limit <= 0) limit = 200;

                List<GameObject> results = new List<GameObject>();
                
                // Get all GameObjects in scene (including inactive). FindObjectsOfType<T>(includeInactive) is
                // 2020.1+; on the floor, FindObjectsOfTypeAll returns inactive too (plus assets/hidden), so filter
                // to scene objects. (COMPATIBILITY.md)
#if UNITY_2020_1_OR_NEWER
                GameObject[] allObjects = GameObject.FindObjectsOfType<GameObject>(true);
#else
                GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>().Where(go => go.scene.IsValid()).ToArray();
#endif
                
                foreach (var obj in allObjects)
                {
                    bool matches = true;
                    
                    // Check name
                    if (!string.IsNullOrEmpty(name))
                    {
                        if (exactMatch)
                        {
                            matches &= obj.name == name;
                        }
                        else
                        {
                            // string.Contains(value, StringComparison) is netstandard2.1+; IndexOf is floor-safe (see COMPATIBILITY.md).
                            matches &= obj.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                    }
                    
                    // Check tag
                    if (!string.IsNullOrEmpty(tag))
                    {
                        matches &= obj.CompareTag(tag);
                    }
                    
                    // Check layer
                    if (layer.HasValue)
                    {
                        matches &= obj.layer == layer.Value;
                    }
                    
                    if (matches)
                    {
                        results.Add(obj);
                    }
                }
                
                // F2: cap the RESPONSE size so a big legacy scene can't blow the 1MB frame budget.
                int total = results.Count;
                bool truncated = total > limit;
                var capped = truncated ? results.Take(limit).ToList() : results;

                // Convert results to data
                var resultData = capped.Select(obj => new
                {
                    id = obj.GetInstanceID(),
                    name = obj.name,
                    path = GetGameObjectPath(obj),
                    tag = obj.tag,
                    layer = obj.layer,
                    isActive = obj.activeSelf,
                    transform = new
                    {
                        position = new { x = obj.transform.position.x, y = obj.transform.position.y, z = obj.transform.position.z },
                        rotation = new { x = obj.transform.rotation.eulerAngles.x, y = obj.transform.rotation.eulerAngles.y, z = obj.transform.rotation.eulerAngles.z },
                        scale = new { x = obj.transform.localScale.x, y = obj.transform.localScale.y, z = obj.transform.localScale.z }
                    }
                }).ToList();
                
                return HandlerOutcome.Ok(new
                {
                    count = resultData.Count,
                    total = total,
                    truncated = truncated,
                    limit = limit,
                    objects = resultData
                });
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to find GameObjects: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Deletes GameObject(s)
        /// </summary>
        public static HandlerOutcome DeleteGameObject(JObject parameters)
        {
            try
            {
                string path = parameters["path"]?.ToString();
                string[] paths = parameters["paths"]?.ToObject<string[]>();
                bool includeChildren = parameters["includeChildren"]?.ToObject<bool>() ?? true;

                // Validate input
                if (string.IsNullOrEmpty(path) && (paths == null || paths.Length == 0))
                {
                    return HandlerOutcome.Fail("Either 'path' or 'paths' parameter is required", "VALIDATION_ERROR");
                }
                
                // Collect all paths
                List<string> allPaths = new List<string>();
                if (!string.IsNullOrEmpty(path))
                {
                    allPaths.Add(path);
                }
                if (paths != null)
                {
                    allPaths.AddRange(paths);
                }
                
                // Find and delete GameObjects
                List<string> deleted = new List<string>();
                List<string> notFound = new List<string>();
                
                foreach (string objPath in allPaths)
                {
                    GameObject obj = GameObject.Find(objPath);
                    if (obj != null)
                    {
                        deleted.Add(objPath);
                        Undo.DestroyObjectImmediate(obj);
                    }
                    else
                    {
                        notFound.Add(objPath);
                    }
                }
                
                return HandlerOutcome.Ok(new
                {
                    deletedCount = deleted.Count,
                    deleted = deleted,
                    notFound = notFound,
                    notFoundCount = notFound.Count
                });
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to delete GameObject: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets the scene hierarchy
        /// </summary>
        public static HandlerOutcome GetHierarchy(JObject parameters)
        {
            try
            {
                bool includeInactive = parameters["includeInactive"]?.ToObject<bool>() ?? true;
                int maxDepth = parameters["maxDepth"]?.ToObject<int>() ?? -1;
                bool includeComponents = parameters["includeComponents"]?.ToObject<bool>() ?? false;
                int maxNodes = parameters["maxNodes"]?.ToObject<int>() ?? 1000;
                if (maxNodes <= 0) maxNodes = 1000;

                // Get root GameObjects
                GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

                // Build hierarchy. F2: a node budget caps the RESPONSE so a big legacy scene can't blow the 1MB
                // frame budget (maxDepth bounds depth, not total breadth).
                var budget = new int[] { maxNodes };
                List<object> hierarchy = new List<object>();
                foreach (var root in rootObjects)
                {
                    if (!includeInactive && !root.activeInHierarchy)
                        continue;
                    if (budget[0] <= 0) break;

                    hierarchy.Add(BuildHierarchyNode(root, 0, maxDepth, includeInactive, includeComponents, budget));
                }
                bool truncated = budget[0] <= 0; // budget exhausted -> there may be more

                return HandlerOutcome.Ok(new
                {
                    sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                    objectCount = hierarchy.Count,
                    truncated = truncated,
                    maxNodes = maxNodes,
                    hierarchy = hierarchy
                });
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to get hierarchy: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Builds a hierarchy node for a GameObject
        /// </summary>
        private static object BuildHierarchyNode(GameObject obj, int currentDepth, int maxDepth, bool includeInactive, bool includeComponents, int[] budget)
        {
            budget[0]--; // count this node against the response budget (F2)
            var node = new Dictionary<string, object>
            {
                ["name"] = obj.name,
                ["path"] = GetGameObjectPath(obj),
                ["isActive"] = obj.activeSelf,
                ["tag"] = obj.tag,
                ["layer"] = obj.layer,
                ["transform"] = new
                {
                    position = new { x = obj.transform.position.x, y = obj.transform.position.y, z = obj.transform.position.z },
                    rotation = new { x = obj.transform.rotation.eulerAngles.x, y = obj.transform.rotation.eulerAngles.y, z = obj.transform.rotation.eulerAngles.z },
                    scale = new { x = obj.transform.localScale.x, y = obj.transform.localScale.y, z = obj.transform.localScale.z }
                }
            };
            
            // Add components if requested
            if (includeComponents)
            {
                var components = obj.GetComponents<Component>();
                var componentList = new List<string>();
                foreach (var comp in components)
                {
                    if (comp != null)
                    {
                        componentList.Add(comp.GetType().Name);
                    }
                }
                node["components"] = componentList;
            }
            
            // Add children if within depth limit
            if (maxDepth < 0 || currentDepth < maxDepth)
            {
                List<object> children = new List<object>();
                foreach (Transform child in obj.transform)
                {
                    if (!includeInactive && !child.gameObject.activeInHierarchy)
                        continue;
                    if (budget[0] <= 0) break; // F2: response budget exhausted

                    children.Add(BuildHierarchyNode(child.gameObject, currentDepth + 1, maxDepth, includeInactive, includeComponents, budget));
                }
                
                if (children.Count > 0)
                {
                    node["children"] = children;
                }
            }
            
            return node;
        }
    }
}