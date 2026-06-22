using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;
// COMPATIBILITY (see COMPATIBILITY.md): PrefabStageUtility moved namespaces in
// Unity 2021.2 — UnityEditor.Experimental.SceneManagement (<= 2021.1) ->
// UnityEditor.SceneManagement (2021.2+). This guarded alias keeps both the
// 2020.3 floor and newer editors compiling. StageUtility is non-experimental
// in all supported versions and is referenced fully-qualified below.
#if UNITY_2021_2_OR_NEWER
using PrefabStageUtility = UnityEditor.SceneManagement.PrefabStageUtility;
#else
using PrefabStageUtility = UnityEditor.Experimental.SceneManagement.PrefabStageUtility;
#endif

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles asset management operations including prefabs, materials, and imports
    /// </summary>
    public static class AssetManagementHandler
    {
        /// <summary>
        /// Creates a new prefab from a GameObject or from scratch
        /// </summary>
        public static HandlerOutcome CreatePrefab(JObject parameters)
        {
            try
            {
                // Parse parameters
                string gameObjectPath = parameters["gameObjectPath"]?.ToString();
                string prefabPath = parameters["prefabPath"]?.ToString();
                bool createFromTemplate = parameters["createFromTemplate"]?.ToObject<bool>() ?? false;
                bool overwrite = parameters["overwrite"]?.ToObject<bool>() ?? false;

                // Validate prefab path
                if (string.IsNullOrEmpty(prefabPath))
                {
                    return HandlerOutcome.Fail("prefabPath is required", "VALIDATION_ERROR");
                }

                if (!prefabPath.StartsWith("Assets/") || !prefabPath.EndsWith(".prefab") || !PathSafety.IsWithinProject(prefabPath))
                {
                    return HandlerOutcome.Fail("prefabPath must be a .prefab inside the project (Assets/...), with no '..' traversal", "VALIDATION_ERROR");
                }

                // Check if prefab already exists
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null && !overwrite)
                {
                    return HandlerOutcome.Fail($"Prefab already exists at {prefabPath}. Set overwrite to true to replace it.", "VALIDATION_ERROR");
                }

                // Ensure directory exists
                string directory = Path.GetDirectoryName(prefabPath);
                if (!AssetDatabase.IsValidFolder(directory))
                {
                    Directory.CreateDirectory(directory);
                    AssetDatabase.Refresh();
                }

                GameObject prefabAsset = null;

                if (createFromTemplate)
                {
                    // Create empty prefab
                    GameObject emptyGO = new GameObject("Empty");
                    prefabAsset = PrefabUtility.SaveAsPrefabAsset(emptyGO, prefabPath);
                    UnityEngine.Object.DestroyImmediate(emptyGO);
                }
                else if (!string.IsNullOrEmpty(gameObjectPath))
                {
                    // Find the GameObject
                    GameObject sourceObject = GameObject.Find(gameObjectPath);
                    if (sourceObject == null)
                    {
                        return HandlerOutcome.Fail($"GameObject not found at path: {gameObjectPath}", "NOT_FOUND");
                    }

                    // Create prefab from GameObject
                    prefabAsset = PrefabUtility.SaveAsPrefabAssetAndConnect(sourceObject, prefabPath, InteractionMode.UserAction);
                }
                else
                {
                    return HandlerOutcome.Fail("Either gameObjectPath or createFromTemplate must be specified", "VALIDATION_ERROR");
                }

                if (prefabAsset == null)
                {
                    return HandlerOutcome.Fail("Failed to create prefab", "INTERNAL_ERROR");
                }

                // Get asset GUID
                string guid = AssetDatabase.AssetPathToGUID(prefabPath);

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    prefabPath = prefabPath,
                    guid = guid,
                    message = createFromTemplate ? "Empty prefab created successfully" : "Prefab created successfully"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in CreatePrefab: {e.Message}");
                return HandlerOutcome.Fail($"Failed to create prefab: {e.Message}");
            }
        }

        /// <summary>
        /// Modifies properties of an existing prefab
        /// </summary>
        public static HandlerOutcome ModifyPrefab(JObject parameters)
        {
            try
            {
                // Parse parameters
                string prefabPath = parameters["prefabPath"]?.ToString();
                { var g = PathSafety.Guard(prefabPath, "prefabPath"); if (g != null) return g; } // H4
                JObject modifications = parameters["modifications"] as JObject;
                bool applyToInstances = parameters["applyToInstances"]?.ToObject<bool>() ?? true;

                // Validate parameters
                if (string.IsNullOrEmpty(prefabPath))
                {
                    return HandlerOutcome.Fail("prefabPath is required", "VALIDATION_ERROR");
                }

                if (modifications == null || !modifications.HasValues)
                {
                    return HandlerOutcome.Fail("modifications object is required and cannot be empty", "VALIDATION_ERROR");
                }

                // Load the prefab
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabAsset == null)
                {
                    return HandlerOutcome.Fail($"Prefab not found at path: {prefabPath}", "NOT_FOUND");
                }

                // Track modifications
                List<string> modifiedProperties = new List<string>();

                // Load prefab contents for editing
                GameObject prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                bool prefabSaved = false;

                try
                {
                    // Apply modifications
                    foreach (var prop in modifications.Properties())
                    {
                        if (ApplyModificationToGameObject(prefabRoot, prop.Name, prop.Value))
                        {
                            modifiedProperties.Add(prop.Name);
                        }
                    }

                    // Save the modified prefab (F1: check the out-success — was ignored, a failed save read as success)
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath, out prefabSaved);
                }
                finally
                {
                    // Unload prefab contents
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }

                if (!prefabSaved)
                    return HandlerOutcome.Fail($"Prefab save failed: {prefabPath}", "INTERNAL_ERROR");

                int affectedInstances = 0;

                // Apply to instances if requested
                if (applyToInstances)
                {
                    GameObject[] allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
                    foreach (var obj in allObjects)
                    {
                        if (PrefabUtility.GetCorrespondingObjectFromSource(obj) == prefabAsset ||
                            PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(obj) == prefabPath)
                        {
                            PrefabUtility.RevertPrefabInstance(obj, InteractionMode.AutomatedAction);
                            affectedInstances++;
                        }
                    }
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    prefabPath = prefabPath,
                    modifiedProperties = modifiedProperties.ToArray(),
                    affectedInstances = affectedInstances,
                    message = modifiedProperties.Count == 0
                        ? "Prefab saved, but NONE of the requested modifications applied" // F1: was "modified successfully"
                        : (applyToInstances ? "Prefab modified successfully" : "Prefab modified without updating instances")
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in ModifyPrefab: {e.Message}");
                return HandlerOutcome.Fail($"Failed to modify prefab: {e.Message}");
            }
        }

        /// <summary>
        /// Instantiates a prefab in the scene
        /// </summary>
        public static HandlerOutcome InstantiatePrefab(JObject parameters)
        {
            try
            {
                // Parse parameters
                string prefabPath = parameters["prefabPath"]?.ToString();
                { var g = PathSafety.Guard(prefabPath, "prefabPath"); if (g != null) return g; } // H4
                var position = ParseVector3(parameters["position"]) ?? Vector3.zero;
                var rotation = ParseVector3(parameters["rotation"]) ?? Vector3.zero;
                string parentPath = parameters["parent"]?.ToString();
                string name = parameters["name"]?.ToString();

                // Validate prefab path
                if (string.IsNullOrEmpty(prefabPath))
                {
                    return HandlerOutcome.Fail("prefabPath is required", "VALIDATION_ERROR");
                }

                // Load the prefab
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabAsset == null)
                {
                    return HandlerOutcome.Fail($"Prefab not found at path: {prefabPath}", "NOT_FOUND");
                }

                // Find parent if specified
                GameObject parent = null;
                if (!string.IsNullOrEmpty(parentPath))
                {
                    parent = GameObject.Find(parentPath);
                    if (parent == null)
                    {
                        return HandlerOutcome.Fail($"Parent GameObject not found at path: {parentPath}", "NOT_FOUND");
                    }
                }

                // Instantiate the prefab
                GameObject instance = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
                if (instance == null)
                {
                    return HandlerOutcome.Fail("Failed to instantiate prefab", "INTERNAL_ERROR");
                }

                // Set transform
                instance.transform.position = position;
                instance.transform.rotation = Quaternion.Euler(rotation);

                // Set parent
                if (parent != null)
                {
                    instance.transform.SetParent(parent.transform, true);
                }

                // Set name if provided
                if (!string.IsNullOrEmpty(name))
                {
                    instance.name = name;
                }

                // Register undo
                Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");

                // Get the path to the created object
                string gameObjectPath = GetGameObjectPath(instance);

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    gameObjectPath = gameObjectPath,
                    prefabPath = prefabPath,
                    position = new { x = position.x, y = position.y, z = position.z },
                    rotation = new { x = rotation.x, y = rotation.y, z = rotation.z },
                    parent = parentPath,
                    name = instance.name,
                    message = "Prefab instantiated successfully"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in InstantiatePrefab: {e.Message}");
                return HandlerOutcome.Fail($"Failed to instantiate prefab: {e.Message}");
            }
        }

        /// <summary>
        /// Creates a ScriptableObject of a named type and saves it as an asset (E1). Pairs with
        /// set_serialized_properties to populate fields (private [SerializeField] included).
        /// </summary>
        public static HandlerOutcome CreateScriptableObject(JObject parameters)
        {
            try
            {
                string typeName = parameters["typeName"]?.ToString();
                string assetPath = parameters["assetPath"]?.ToString();
                bool overwrite = parameters["overwrite"]?.ToObject<bool>() ?? false;

                if (string.IsNullOrEmpty(typeName))
                    return HandlerOutcome.Fail("typeName is required", "VALIDATION_ERROR");
                if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/") || !assetPath.EndsWith(".asset") || !PathSafety.IsWithinProject(assetPath))
                    return HandlerOutcome.Fail("assetPath must be a .asset inside the project (Assets/...), with no '..' traversal", "VALIDATION_ERROR");

                var type = ManagedReferenceResolver.ResolveType(typeName);
                if (type == null)
                    return HandlerOutcome.Fail($"type not found: {typeName}", "TYPE_NOT_FOUND");
                if (!typeof(ScriptableObject).IsAssignableFrom(type))
                    return HandlerOutcome.Fail($"{type.FullName} is not a ScriptableObject", "NOT_A_SCRIPTABLE_OBJECT");
                if (type.IsAbstract || type.IsGenericTypeDefinition)
                    return HandlerOutcome.Fail($"{type.FullName} is abstract or an open generic and cannot be instantiated", "NOT_INSTANTIABLE");

                bool confirm = parameters["confirm"]?.ToObject<bool>() ?? false;
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
                {
                    if (!overwrite)
                        return HandlerOutcome.Fail($"Asset already exists at {assetPath}. Set overwrite to true to replace it.", "PATH_EXISTS");
                    // Overwrite is a destructive replace — same dependents gate as delete (H3); bail BEFORE creating the SO.
                    var gate = GuardDestructiveReplace(assetPath, confirm);
                    if (gate != null) return gate;
                    if (!AssetDatabase.DeleteAsset(assetPath))
                        return HandlerOutcome.Fail($"Could not replace existing asset at {assetPath}", "INVALID_STATE");
                }

                string directory = Path.GetDirectoryName(assetPath);
                if (!AssetDatabase.IsValidFolder(directory)) { Directory.CreateDirectory(directory); AssetDatabase.Refresh(); }

                var so = ScriptableObject.CreateInstance(type);
                if (so == null)
                    return HandlerOutcome.Fail($"Failed to instantiate {type.FullName}", "INTERNAL_ERROR");
                AssetDatabase.CreateAsset(so, assetPath);
                AssetDatabase.SaveAssets();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    assetPath = assetPath,
                    guid = AssetDatabase.AssetPathToGUID(assetPath),
                    type = type.FullName,
                    message = "ScriptableObject created"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in CreateScriptableObject: {e.Message}");
                return HandlerOutcome.Fail($"Failed to create ScriptableObject: {e.Message}");
            }
        }

        /// <summary>Unpacks a prefab instance (E2): regular = outermost root, complete = whole hierarchy.</summary>
        public static HandlerOutcome UnpackPrefab(JObject parameters)
        {
            try
            {
                if (EditorApplication.isPlaying)
                    return HandlerOutcome.Fail("scene mutations refuse in play mode", "PLAY_MODE");

                string gameObjectPath = parameters["gameObjectPath"]?.ToString();
                int? instanceId = parameters["instanceId"]?.ToObject<int?>();
                string modeStr = (parameters["mode"]?.ToString() ?? "regular").ToLower();

                GameObject go = null;
                if (instanceId.HasValue) go = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                else if (!string.IsNullOrEmpty(gameObjectPath)) go = GameObject.Find(gameObjectPath);
                if (go == null)
                    return HandlerOutcome.Fail("GameObject not found (provide gameObjectPath or instanceId)", "NOT_FOUND");
                if (!PrefabUtility.IsPartOfPrefabInstance(go))
                    return HandlerOutcome.Fail($"{go.name} is not a prefab instance", "NOT_A_PREFAB_INSTANCE");
                if (PrefabUtility.GetOutermostPrefabInstanceRoot(go) != go)
                    return HandlerOutcome.Fail($"{go.name} is not the outermost prefab-instance root; unpack the root instead", "NOT_OUTERMOST_ROOT");

                var mode = modeStr == "complete" ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
                PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.UserAction); // UserAction registers Undo

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    gameObjectPath = GetGameObjectPath(go),
                    mode = modeStr,
                    message = "Prefab instance unpacked"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in UnpackPrefab: {e.Message}");
                return HandlerOutcome.Fail($"Failed to unpack prefab: {e.Message}");
            }
        }

        /// <summary>Creates a prefab variant of a base prefab (E2).</summary>
        public static HandlerOutcome CreatePrefabVariant(JObject parameters)
        {
            try
            {
                string basePrefabPath = parameters["basePrefabPath"]?.ToString();
                string baseGuid = parameters["baseGuid"]?.ToString();
                string variantPath = parameters["variantPath"]?.ToString();
                bool overwrite = parameters["overwrite"]?.ToObject<bool>() ?? false;

                if (!string.IsNullOrEmpty(baseGuid) && string.IsNullOrEmpty(basePrefabPath))
                    basePrefabPath = AssetDatabase.GUIDToAssetPath(baseGuid);
                if (string.IsNullOrEmpty(basePrefabPath))
                    return HandlerOutcome.Fail("basePrefabPath or baseGuid is required", "VALIDATION_ERROR");
                { var g = PathSafety.Guard(basePrefabPath, "basePrefabPath"); if (g != null) return g; } // H4
                if (string.IsNullOrEmpty(variantPath) || !variantPath.StartsWith("Assets/") || !variantPath.EndsWith(".prefab") || !PathSafety.IsWithinProject(variantPath))
                    return HandlerOutcome.Fail("variantPath must be a .prefab inside the project (Assets/...), with no '..' traversal", "VALIDATION_ERROR");

                var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(basePrefabPath);
                if (basePrefab == null)
                    return HandlerOutcome.Fail($"Base prefab not found: {basePrefabPath}", "NOT_FOUND");
                bool confirm = parameters["confirm"]?.ToObject<bool>() ?? false;
                if (AssetDatabase.LoadAssetAtPath<GameObject>(variantPath) != null)
                {
                    if (!overwrite)
                        return HandlerOutcome.Fail($"Asset already exists at {variantPath}. Set overwrite to true to replace it.", "PATH_EXISTS");
                    var gate = GuardDestructiveReplace(variantPath, confirm);
                    if (gate != null) return gate;
                    if (!AssetDatabase.DeleteAsset(variantPath))
                        return HandlerOutcome.Fail($"Could not replace existing variant at {variantPath}", "INVALID_STATE");
                }

                string directory = Path.GetDirectoryName(variantPath);
                if (!AssetDatabase.IsValidFolder(directory)) { Directory.CreateDirectory(directory); AssetDatabase.Refresh(); }

                var instance = PrefabUtility.InstantiatePrefab(basePrefab) as GameObject;
                if (instance == null)
                    return HandlerOutcome.Fail("Failed to instantiate the base prefab", "INTERNAL_ERROR");
                GameObject variant = null;
                try
                {
                    // Saving a CONNECTED prefab instance as an asset creates a Prefab VARIANT of its source.
                    variant = PrefabUtility.SaveAsPrefabAsset(instance, variantPath);
                }
                finally { UnityEngine.Object.DestroyImmediate(instance); }

                if (variant == null)
                    return HandlerOutcome.Fail("Failed to create prefab variant", "INTERNAL_ERROR");
                AssetDatabase.SaveAssets();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    variantPath = variantPath,
                    guid = AssetDatabase.AssetPathToGUID(variantPath),
                    basePrefabPath = basePrefabPath,
                    message = "Prefab variant created"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in CreatePrefabVariant: {e.Message}");
                return HandlerOutcome.Fail($"Failed to create prefab variant: {e.Message}");
            }
        }

        // Destructive-replace gate (H3): refuse to overwrite an asset that has dependents unless confirm:true,
        // returning the dependent list (a dry-run). Returns null when it is safe to proceed (no deps or confirmed).
        private static HandlerOutcome GuardDestructiveReplace(string assetPath, bool confirm)
        {
            if (confirm) return null;
            var dependents = AssetDatabaseHandler.FindDependents(assetPath);
            if (dependents.Count == 0) return null;
            // Consistent with the central H3 gate: a refusal is an ERROR (CONFIRMATION_REQUIRED), not a success
            // envelope — so an agent that keys on the error code recognises it. Dependents go in details.
            return HandlerOutcome.Fail(
                $"Overwriting '{assetPath}' would affect {dependents.Count} dependent(s). Re-call with confirm:true to replace.",
                "CONFIRMATION_REQUIRED",
                details: new { wouldReplace = assetPath, dependents = dependents, dependentCount = dependents.Count });
        }

        /// <summary>
        /// Creates a new material with specified shader and properties
        /// </summary>
        public static HandlerOutcome CreateMaterial(JObject parameters)
        {
            try
            {
                // Parse parameters
                string materialPath = parameters["materialPath"]?.ToString();
                string shader = parameters["shader"]?.ToString() ?? "Standard";
                JObject properties = parameters["properties"] as JObject;
                string copyFrom = parameters["copyFrom"]?.ToString();
                { var g = PathSafety.Guard(copyFrom, "copyFrom"); if (g != null) return g; } // H4 (copyFrom optional)
                bool overwrite = parameters["overwrite"]?.ToObject<bool>() ?? false;

                // Validate material path
                if (string.IsNullOrEmpty(materialPath))
                {
                    return HandlerOutcome.Fail("materialPath is required", "VALIDATION_ERROR");
                }

                if (!materialPath.StartsWith("Assets/") || !materialPath.EndsWith(".mat") || !PathSafety.IsWithinProject(materialPath))
                {
                    return HandlerOutcome.Fail("materialPath must be a .mat inside the project (Assets/...), with no '..' traversal", "VALIDATION_ERROR");
                }

                // Check if material already exists
                if (AssetDatabase.LoadAssetAtPath<Material>(materialPath) != null && !overwrite)
                {
                    return HandlerOutcome.Fail($"Material already exists at {materialPath}. Set overwrite to true to replace it.", "VALIDATION_ERROR");
                }

                // Ensure directory exists
                string directory = Path.GetDirectoryName(materialPath);
                if (!AssetDatabase.IsValidFolder(directory))
                {
                    Directory.CreateDirectory(directory);
                    AssetDatabase.Refresh();
                }

                Material material = null;
                List<string> propertiesSet = new List<string>();

                if (!string.IsNullOrEmpty(copyFrom))
                {
                    // Copy from existing material
                    Material sourceMaterial = AssetDatabase.LoadAssetAtPath<Material>(copyFrom);
                    if (sourceMaterial == null)
                    {
                        return HandlerOutcome.Fail($"Source material not found at: {copyFrom}", "NOT_FOUND");
                    }

                    material = new Material(sourceMaterial);
                }
                else
                {
                    // Find shader
                    Shader shaderAsset = Shader.Find(shader);
                    if (shaderAsset == null)
                    {
                        return HandlerOutcome.Fail($"Shader not found: {shader}", "NOT_FOUND");
                    }

                    material = new Material(shaderAsset);
                }

                // Apply properties if provided
                if (properties != null && properties.HasValues)
                {
                    foreach (var prop in properties.Properties())
                    {
                        if (ApplyMaterialProperty(material, prop.Name, prop.Value))
                        {
                            propertiesSet.Add(prop.Name);
                        }
                    }
                }

                // Save the material
                AssetDatabase.CreateAsset(material, materialPath);
                AssetDatabase.SaveAssets();

                // Get asset GUID
                string guid = AssetDatabase.AssetPathToGUID(materialPath);

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    materialPath = materialPath,
                    shader = material.shader.name,
                    guid = guid,
                    propertiesSet = propertiesSet.ToArray(),
                    copiedFrom = copyFrom,
                    message = !string.IsNullOrEmpty(copyFrom) ?
                        "Material created from copy successfully" :
                        "Material created successfully"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in CreateMaterial: {e.Message}");
                return HandlerOutcome.Fail($"Failed to create material: {e.Message}");
            }
        }

        /// <summary>
        /// Modifies properties of an existing material
        /// </summary>
        public static HandlerOutcome ModifyMaterial(JObject parameters)
        {
            try
            {
                // Parse parameters
                string materialPath = parameters["materialPath"]?.ToString();
                JObject properties = parameters["properties"] as JObject;
                string shader = parameters["shader"]?.ToString();

                // Validate material path
                if (string.IsNullOrEmpty(materialPath))
                {
                    return HandlerOutcome.Fail("materialPath is required", "VALIDATION_ERROR");
                }

                if (!materialPath.StartsWith("Assets/") || !materialPath.EndsWith(".mat") || !PathSafety.IsWithinProject(materialPath))
                {
                    return HandlerOutcome.Fail("materialPath must be a .mat inside the project (Assets/...), with no '..' traversal", "VALIDATION_ERROR");
                }

                // Validate properties
                if (properties == null || !properties.HasValues)
                {
                    return HandlerOutcome.Fail("properties object is required and cannot be empty", "VALIDATION_ERROR");
                }

                // Load the material
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    return HandlerOutcome.Fail($"Material not found at path: {materialPath}", "NOT_FOUND");
                }

                List<string> propertiesModified = new List<string>();
                List<string> propertiesFailed = new List<string>();
                bool shaderChanged = false;
                string previousShader = material.shader.name;

                Undo.RecordObject(material, "Modify Material"); // F1: record BEFORE mutating (was no Undo)

                // Change shader if specified
                if (!string.IsNullOrEmpty(shader))
                {
                    Shader newShader = Shader.Find(shader);
                    if (newShader == null)
                    {
                        return HandlerOutcome.Fail($"Shader not found: {shader}", "NOT_FOUND");
                    }

                    if (material.shader != newShader)
                    {
                        material.shader = newShader;
                        shaderChanged = true;
                    }
                }

                // Apply property modifications
                foreach (var prop in properties.Properties())
                {
                    if (ApplyMaterialProperty(material, prop.Name, prop.Value))
                        propertiesModified.Add(prop.Name);
                    else
                        propertiesFailed.Add(prop.Name);
                }

                // F1: don't report success when NOTHING applied (was success:true with propertiesModified:[]).
                if (!shaderChanged && propertiesModified.Count == 0)
                    return HandlerOutcome.Fail($"No changes applied — none of the {propertiesFailed.Count} requested property(ies) could be set on this material/shader", "INVALID_STATE");

                // Mark as dirty to ensure changes are saved
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    materialPath = materialPath,
                    propertiesModified = propertiesModified.ToArray(),
                    propertiesFailed = propertiesFailed.ToArray(),
                    shaderChanged = shaderChanged,
                    previousShader = shaderChanged ? previousShader : null,
                    newShader = shaderChanged ? shader : null,
                    message = propertiesFailed.Count > 0 ? "Material modified (some requested properties were not applied — see propertiesFailed)" : "Material modified successfully"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in ModifyMaterial: {e.Message}");
                return HandlerOutcome.Fail($"Failed to modify material: {e.Message}");
            }
        }

        #region Helper Methods

        private static bool ApplyMaterialProperty(Material material, string propertyName, JToken value)
        {
            try
            {
                // Check if property exists
                if (!material.HasProperty(propertyName))
                {
                    Debug.LogWarning($"Material does not have property: {propertyName}");
                    return false;
                }

                // Get property type and apply value
                int propId = Shader.PropertyToID(propertyName);
                
                // Try to determine property type by value
                if (value.Type == JTokenType.Array)
                {
                    var array = value as JArray;
                    if (array != null)
                    {
                        if (array.Count == 4)
                        {
                            // Color property
                            material.SetColor(propId, new Color(
                                array[0].ToObject<float>(),
                                array[1].ToObject<float>(),
                                array[2].ToObject<float>(),
                                array[3].ToObject<float>()
                            ));
                            return true;
                        }
                        else if (array.Count == 3)
                        {
                            // Vector3 property
                            material.SetVector(propId, new Vector4(
                                array[0].ToObject<float>(),
                                array[1].ToObject<float>(),
                                array[2].ToObject<float>(),
                                0
                            ));
                            return true;
                        }
                        else if (array.Count == 2)
                        {
                            // Vector2 property
                            material.SetVector(propId, new Vector4(
                                array[0].ToObject<float>(),
                                array[1].ToObject<float>(),
                                0, 0
                            ));
                            return true;
                        }
                    }
                }
                else if (value.Type == JTokenType.Float || value.Type == JTokenType.Integer)
                {
                    // Float property
                    material.SetFloat(propId, value.ToObject<float>());
                    return true;
                }
                else if (value.Type == JTokenType.String)
                {
                    // Could be a texture reference
                    string texturePath = value.ToString();
                    if (texturePath.StartsWith("Assets/"))
                    {
                        Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                        if (texture != null)
                        {
                            material.SetTexture(propId, texture);
                            return true;
                        }
                    }
                }
                else if (value.Type == JTokenType.Boolean)
                {
                    // Boolean as float (0 or 1)
                    material.SetFloat(propId, value.ToObject<bool>() ? 1f : 0f);
                    return true;
                }

                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to set material property {propertyName}: {e.Message}");
                return false;
            }
        }

        private static Vector3? ParseVector3(JToken token)
        {
            if (token == null) return null;
            
            try
            {
                float x = token["x"]?.ToObject<float>() ?? 0f;
                float y = token["y"]?.ToObject<float>() ?? 0f;
                float z = token["z"]?.ToObject<float>() ?? 0f;
                return new Vector3(x, y, z);
            }
            catch
            {
                return null;
            }
        }

        private static bool ApplyModificationToGameObject(GameObject target, string propertyName, JToken value)
        {
            try
            {
                switch (propertyName.ToLower())
                {
                    case "name":
                        target.name = value.ToString();
                        return true;

                    case "tag":
                        target.tag = value.ToString();
                        return true;

                    case "layer":
                        int layer = value.ToObject<int>();
                        if (layer >= 0 && layer < 32)
                        {
                            target.layer = layer;
                            return true;
                        }
                        break;

                    case "active":
                    case "isactive":
                        target.SetActive(value.ToObject<bool>());
                        return true;

                    case "transform":
                        var transformData = value as JObject;
                        if (transformData != null)
                        {
                            if (transformData["position"] != null)
                            {
                                var pos = ParseVector3(transformData["position"]);
                                if (pos.HasValue) target.transform.localPosition = pos.Value;
                            }
                            if (transformData["rotation"] != null)
                            {
                                var rot = ParseVector3(transformData["rotation"]);
                                if (rot.HasValue) target.transform.localRotation = Quaternion.Euler(rot.Value);
                            }
                            if (transformData["scale"] != null)
                            {
                                var scale = ParseVector3(transformData["scale"]);
                                if (scale.HasValue) target.transform.localScale = scale.Value;
                            }
                            return true;
                        }
                        break;

                    case "components":
                        var componentsData = value as JObject;
                        if (componentsData != null)
                        {
                            foreach (var comp in componentsData.Properties())
                            {
                                ApplyComponentModification(target, comp.Name, comp.Value as JObject);
                            }
                            return true;
                        }
                        break;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyComponentModification(GameObject target, string componentType, JObject properties)
        {
            if (properties == null) return;

            var component = target.GetComponent(componentType);
            if (component == null) return;

            var type = component.GetType();
            
            foreach (var prop in properties.Properties())
            {
                try
                {
                    var field = type.GetField(prop.Name);
                    if (field != null && field.IsPublic)
                    {
                        field.SetValue(component, prop.Value.ToObject(field.FieldType));
                        continue;
                    }

                    var property = type.GetProperty(prop.Name);
                    if (property != null && property.CanWrite)
                    {
                        property.SetValue(component, prop.Value.ToObject(property.PropertyType));
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to set {prop.Name} on {componentType}: {e.Message}");
                }
            }
        }

        private static string GetGameObjectPath(GameObject obj)
        {
            string path = "/" + obj.name;
            Transform parent = obj.transform.parent;
            
            while (parent != null)
            {
                path = "/" + parent.name + path;
                parent = parent.parent;
            }
            
            return path;
        }

        #endregion

        /// <summary>
        /// Opens a prefab in prefab mode for editing
        /// </summary>
        public static HandlerOutcome OpenPrefab(JObject parameters)
        {
            try
            {
                // Parse parameters
                string prefabPath = parameters["prefabPath"]?.ToString();
                { var g = PathSafety.Guard(prefabPath, "prefabPath"); if (g != null) return g; } // H4
                string focusObject = parameters["focusObject"]?.ToString();
                bool isolateObject = parameters["isolateObject"]?.ToObject<bool>() ?? false;

                // Validate prefab path
                if (string.IsNullOrEmpty(prefabPath))
                {
                    return HandlerOutcome.Fail("prefabPath is required", "VALIDATION_ERROR");
                }

                // Load the prefab asset
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabAsset == null)
                {
                    return HandlerOutcome.Fail($"Prefab asset not found at path: {prefabPath}", "NOT_FOUND");
                }

                // Check if asset is actually a prefab
                if (!PrefabUtility.IsPartOfPrefabAsset(prefabAsset))
                {
                    return HandlerOutcome.Fail($"Asset at path is not a prefab: {prefabPath}", "VALIDATION_ERROR");
                }

                // Check if already in prefab mode with this prefab
                var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
                bool wasAlreadyOpen = false;

                if (currentStage != null && currentStage.assetPath == prefabPath)
                {
                    wasAlreadyOpen = true;
                }
                else
                {
                    // Open the prefab in prefab mode. NOTE (audit #33): on the 2020.3 floor there is no
                    // public synchronous prefab-stage open — PrefabStageUtility.OpenPrefab is not in the
                    // Experimental.SceneManagement namespace — so AssetDatabase.OpenAsset is used and the
                    // stage is read back below; if it is not ready this frame the caller can retry.
                    AssetDatabase.OpenAsset(prefabAsset);

                    // Read the (possibly just-opened) prefab stage.
                    currentStage = PrefabStageUtility.GetCurrentPrefabStage();
                }

                if (currentStage == null)
                {
                    return HandlerOutcome.Fail("Failed to enter prefab mode", "INVALID_STATE");
                }

                GameObject prefabRoot = currentStage.prefabContentsRoot;
                string focusedObjectPath = null;

                // Focus on specific object if requested
                if (!string.IsNullOrEmpty(focusObject) && prefabRoot != null)
                {
                    Transform focusTransform = prefabRoot.transform.Find(focusObject.TrimStart('/'));
                    if (focusTransform != null)
                    {
                        Selection.activeGameObject = focusTransform.gameObject;
                        EditorGUIUtility.PingObject(focusTransform.gameObject);
                        focusedObjectPath = GetGameObjectPath(focusTransform.gameObject);

                        if (isolateObject)
                        {
                            // Use scene visibility to isolate object
                            UnityEditor.SceneVisibilityManager.instance.Isolate(focusTransform.gameObject, true);
                        }
                    }
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    prefabPath = prefabPath,
                    isInPrefabMode = true,
                    prefabContentsRoot = GetGameObjectPath(prefabRoot),
                    focusedObject = focusedObjectPath,
                    wasAlreadyOpen = wasAlreadyOpen,
                    message = wasAlreadyOpen ? "Already editing this prefab" : "Prefab opened in prefab mode"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in OpenPrefab: {e.Message}");
                return HandlerOutcome.Fail($"Failed to open prefab: {e.Message}");
            }
        }

        /// <summary>
        /// Exits prefab mode and optionally saves changes
        /// </summary>
        public static HandlerOutcome ExitPrefabMode(JObject parameters)
        {
            try
            {
                // Parse parameters
                bool saveChanges = parameters["saveChanges"]?.ToObject<bool>() ?? true;

                // Check if in prefab mode
                var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
                if (currentStage == null)
                {
                    return HandlerOutcome.Ok(new
                    {
                        success = true,
                        wasInPrefabMode = false,
                        message = "Not currently in prefab mode"
                    });
                }

                string prefabPath = currentStage.assetPath;
                bool changesSaved = false;

                // Save changes if requested
                if (saveChanges && currentStage.scene.isDirty)
                {
                    try
                    {
                        PrefabUtility.SaveAsPrefabAsset(currentStage.prefabContentsRoot, prefabPath);
                        changesSaved = true;
                    }
                    catch (Exception saveEx)
                    {
                        return HandlerOutcome.Fail($"Failed to save prefab changes: {saveEx.Message}", "INTERNAL_ERROR");
                    }
                }

                // Exit prefab mode
                UnityEditor.SceneManagement.StageUtility.GoBackToPreviousStage();

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    wasInPrefabMode = true,
                    changesSaved = changesSaved,
                    prefabPath = prefabPath,
                    message = changesSaved ? "Exited prefab mode and saved changes" : "Exited prefab mode without saving changes"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in ExitPrefabMode: {e.Message}");
                return HandlerOutcome.Fail($"Failed to exit prefab mode: {e.Message}");
            }
        }

        /// <summary>
        /// Saves current prefab changes or applies overrides from a prefab instance
        /// </summary>
        public static HandlerOutcome SavePrefab(JObject parameters)
        {
            try
            {
                // Parse parameters
                string gameObjectPath = parameters["gameObjectPath"]?.ToString();
                bool includeChildren = parameters["includeChildren"]?.ToObject<bool>() ?? true;

                // Check if in prefab mode
                var currentStage = PrefabStageUtility.GetCurrentPrefabStage();

                if (currentStage != null && string.IsNullOrEmpty(gameObjectPath))
                {
                    // Save current prefab in prefab mode
                    string prefabPath = currentStage.assetPath;

                    if (currentStage.scene.isDirty)
                    {
                        PrefabUtility.SaveAsPrefabAsset(currentStage.prefabContentsRoot, prefabPath);

                        return HandlerOutcome.Ok(new
                        {
                            success = true,
                            savedInPrefabMode = true,
                            prefabPath = prefabPath,
                            message = "Prefab changes saved successfully"
                        });
                    }
                    else
                    {
                        return HandlerOutcome.Ok(new
                        {
                            success = true,
                            savedInPrefabMode = true,
                            prefabPath = prefabPath,
                            message = "No changes to save"
                        });
                    }
                }
                else if (!string.IsNullOrEmpty(gameObjectPath))
                {
                    // Save prefab instance overrides
                    GameObject gameObject = GameObject.Find(gameObjectPath);
                    if (gameObject == null)
                    {
                        return HandlerOutcome.Fail($"GameObject not found at path: {gameObjectPath}", "NOT_FOUND");
                    }

                    // Check if it's a prefab instance
                    if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
                    {
                        return HandlerOutcome.Fail($"GameObject is not a prefab instance: {gameObjectPath}", "INVALID_STATE");
                    }

                    // Get prefab path
                    string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);

                    // Count overrides before applying
                    var overrides = PrefabUtility.GetObjectOverrides(gameObject, includeChildren);
                    int overrideCount = overrides.Count;

                    // Apply overrides
                    if (includeChildren)
                    {
                        PrefabUtility.ApplyPrefabInstance(gameObject, InteractionMode.UserAction);
                    }
                    else
                    {
                        // Apply only root object overrides
                        PrefabUtility.ApplyObjectOverride(gameObject, AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(gameObject)), InteractionMode.UserAction);
                    }

                    return HandlerOutcome.Ok(new
                    {
                        success = true,
                        gameObjectPath = gameObjectPath,
                        prefabPath = prefabPath,
                        overridesApplied = overrideCount,
                        includedChildren = includeChildren,
                        message = $"Applied {overrideCount} overrides to prefab"
                    });
                }
                else
                {
                    return HandlerOutcome.Fail("Not currently in prefab mode and no gameObjectPath specified", "INVALID_STATE");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in SavePrefab: {e.Message}");
                return HandlerOutcome.Fail($"Failed to save prefab: {e.Message}");
            }
        }

        // ===== Granular prefab-override apply/revert (E-tail) — manage_prefab_overrides =====
        public static HandlerOutcome ManagePrefabOverrides(JObject parameters)
        {
            try
            {
                string action = (parameters["action"]?.ToString() ?? "list").ToLowerInvariant();
                string gameObjectPath = parameters["gameObjectPath"]?.ToString();
                if (string.IsNullOrEmpty(gameObjectPath))
                    return HandlerOutcome.Fail("gameObjectPath is required", "VALIDATION_ERROR");

                var go = GameObject.Find(gameObjectPath);
                if (go == null)
                    return HandlerOutcome.Fail($"GameObject not found at path: {gameObjectPath}", "NOT_FOUND");
                if (!PrefabUtility.IsPartOfPrefabInstance(go))
                    return HandlerOutcome.Fail($"GameObject is not a prefab instance: {gameObjectPath}", "INVALID_STATE");

                switch (action)
                {
                    case "list":
                        return ListPrefabOverrides(go, gameObjectPath, parameters);
                    case "apply_all":
                    case "revert_all":
                    case "apply_property":
                    case "revert_property":
                        if (EditorApplication.isPlaying)
                            return HandlerOutcome.Fail("Prefab override mutations refuse in play mode", "PLAY_MODE");
                        if (action == "apply_all") return ApplyAllOverrides(go, gameObjectPath);
                        if (action == "revert_all") return RevertAllOverrides(go, gameObjectPath);
                        return ApplyOrRevertProperty(go, gameObjectPath, parameters, action == "apply_property");
                    default:
                        return HandlerOutcome.Fail($"Unknown action '{action}'. Use list, apply_property, revert_property, apply_all, or revert_all.", "VALIDATION_ERROR");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetManagementHandler] Error in ManagePrefabOverrides: {e.Message}");
                return HandlerOutcome.Fail($"manage_prefab_overrides failed: {e.Message}");
            }
        }

        private static HandlerOutcome ListPrefabOverrides(GameObject go, string gameObjectPath, JObject parameters)
        {
            int limit = parameters["limit"]?.ToObject<int?>() ?? 100;
            if (limit < 1) limit = 1;

            var mods = PrefabUtility.GetPropertyModifications(go) ?? new PropertyModification[0];
            var modList = new List<object>();
            int total = 0;
            foreach (var m in mods)
            {
                if (m == null || m.target == null) continue;
                total++;
                if (modList.Count < limit)
                {
                    modList.Add(new
                    {
                        target = m.target.GetType().Name,
                        propertyPath = m.propertyPath,
                        value = m.value,
                        objectReference = m.objectReference != null ? m.objectReference.name : null
                    });
                }
            }

            return HandlerOutcome.Ok(new
            {
                success = true,
                action = "list",
                gameObjectPath,
                prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go),
                propertyModifications = modList,
                propertyModificationCount = total,
                truncated = total > modList.Count,
                addedComponents = PrefabUtility.GetAddedComponents(go).Count,
                removedComponents = PrefabUtility.GetRemovedComponents(go).Count,
                addedGameObjects = PrefabUtility.GetAddedGameObjects(go).Count
            });
        }

        private static HandlerOutcome ApplyAllOverrides(GameObject go, string gameObjectPath)
        {
            int count = PrefabUtility.GetObjectOverrides(go, true).Count;
            PrefabUtility.ApplyPrefabInstance(go, InteractionMode.UserAction);
            return HandlerOutcome.Ok(new
            {
                success = true,
                action = "apply_all",
                gameObjectPath,
                overridesApplied = count,
                message = "Applied all overrides to the prefab source"
            });
        }

        private static HandlerOutcome RevertAllOverrides(GameObject go, string gameObjectPath)
        {
            PrefabUtility.RevertPrefabInstance(go, InteractionMode.UserAction);
            return HandlerOutcome.Ok(new
            {
                success = true,
                action = "revert_all",
                gameObjectPath,
                message = "Reverted all overrides to the prefab source values"
            });
        }

        private static HandlerOutcome ApplyOrRevertProperty(GameObject go, string gameObjectPath, JObject parameters, bool apply)
        {
            string propertyPath = parameters["propertyPath"]?.ToString();
            if (string.IsNullOrEmpty(propertyPath))
                return HandlerOutcome.Fail("propertyPath is required for apply_property/revert_property", "VALIDATION_ERROR");
            string componentType = parameters["componentType"]?.ToString();

            UnityEngine.Object target;
            if (string.IsNullOrEmpty(componentType) || componentType.Equals("GameObject", StringComparison.OrdinalIgnoreCase))
            {
                target = go;
            }
            else
            {
                target = FindOverrideComponent(go, componentType);
                if (target == null)
                    return HandlerOutcome.Fail($"Component not found on instance: {componentType}", "NOT_FOUND");
            }

            var so = new SerializedObject(target);
            var prop = so.FindProperty(propertyPath);
            if (prop == null)
                return HandlerOutcome.Fail($"Serialized property not found: {propertyPath} (on {componentType ?? "GameObject"})", "NOT_FOUND");

            if (apply)
            {
                string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                PrefabUtility.ApplyPropertyOverride(prop, prefabPath, InteractionMode.UserAction);
                return HandlerOutcome.Ok(new
                {
                    success = true,
                    action = "apply_property",
                    gameObjectPath,
                    componentType = componentType ?? "GameObject",
                    propertyPath,
                    prefabPath,
                    message = $"Applied override '{propertyPath}' to the prefab source"
                });
            }

            PrefabUtility.RevertPropertyOverride(prop, InteractionMode.UserAction);
            return HandlerOutcome.Ok(new
            {
                success = true,
                action = "revert_property",
                gameObjectPath,
                componentType = componentType ?? "GameObject",
                propertyPath,
                message = $"Reverted override '{propertyPath}' to the prefab source value"
            });
        }

        private static Component FindOverrideComponent(GameObject go, string typeName)
        {
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var t = c.GetType();
                if (t.Name == typeName || t.FullName == typeName) return c;
            }
            return null;
        }
    }
}