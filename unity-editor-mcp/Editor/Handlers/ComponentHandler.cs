using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles component-related operations on GameObjects
    /// </summary>
    public static class ComponentHandler
    {
        /// <summary>
        /// Adds a component to a GameObject
        /// </summary>
        public static object AddComponent(JObject parameters)
        {
            try
            {
                // Parse parameters
                string gameObjectPath = parameters["gameObjectPath"]?.ToString();
                string componentType = parameters["componentType"]?.ToString();
                JObject properties = parameters["properties"] as JObject;

                // Validate parameters
                if (string.IsNullOrEmpty(gameObjectPath))
                {
                    return new { error = "gameObjectPath is required" };
                }

                if (string.IsNullOrEmpty(componentType))
                {
                    return new { error = "componentType is required" };
                }

                // Find GameObject
                GameObject targetObject = GameObject.Find(gameObjectPath);
                if (targetObject == null)
                {
                    return new { error = $"GameObject not found: {gameObjectPath}" };
                }

                // Resolve component type
                Type type = ResolveComponentType(componentType);
                if (type == null)
                {
                    return new { error = $"Component type not found: {componentType}" };
                }

                // Check if component already exists (for unique components)
                if (targetObject.GetComponent(type) != null && IsUniqueComponent(type))
                {
                    return new { error = $"GameObject already has component: {componentType}" };
                }

                // Add the component
                Component newComponent = targetObject.AddComponent(type);
                if (newComponent == null)
                {
                    return new { error = $"Failed to add component: {componentType}" };
                }

                // Register the creation undo immediately after creating the component (idiomatic order),
                // before applying properties — undo destroys the whole component, so its applied
                // properties go with it. (Audit finding #29.)
                Undo.RegisterCreatedObjectUndo(newComponent, $"Add {componentType}");

                // Apply properties if provided
                var appliedProperties = new List<string>();
                if (properties != null && properties.HasValues)
                {
                    foreach (var prop in properties.Properties())
                    {
                        if (SetComponentProperty(newComponent, prop.Name, prop.Value))
                        {
                            appliedProperties.Add(prop.Name);
                        }
                    }
                }

                return new
                {
                    success = true,
                    componentType = type.Name,
                    gameObjectPath = gameObjectPath,
                    message = $"Component {type.Name} added successfully",
                    appliedProperties = appliedProperties.ToArray()
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ComponentHandler] Error in AddComponent: {ex.Message}");
                return new { error = $"Failed to add component: {ex.Message}" };
            }
        }

        /// <summary>
        /// Removes a component from a GameObject
        /// </summary>
        public static object RemoveComponent(JObject parameters)
        {
            try
            {
                // Parse parameters
                string gameObjectPath = parameters["gameObjectPath"]?.ToString();
                string componentType = parameters["componentType"]?.ToString();
                int componentIndex = parameters["componentIndex"]?.ToObject<int>() ?? 0;

                // Validate parameters
                if (string.IsNullOrEmpty(gameObjectPath))
                {
                    return new { error = "gameObjectPath is required" };
                }

                if (string.IsNullOrEmpty(componentType))
                {
                    return new { error = "componentType is required" };
                }

                // Find GameObject
                GameObject targetObject = GameObject.Find(gameObjectPath);
                if (targetObject == null)
                {
                    return new { error = $"GameObject not found: {gameObjectPath}" };
                }

                // Resolve component type
                Type type = ResolveComponentType(componentType);
                if (type == null)
                {
                    return new { error = $"Component type not found: {componentType}" };
                }

                // Special handling for Transform
                if (type == typeof(Transform))
                {
                    return new { error = "Cannot remove Transform component" };
                }

                // Get all components of the type
                Component[] components = targetObject.GetComponents(type);
                if (components.Length == 0)
                {
                    return new
                    {
                        success = true,
                        removed = false,
                        componentType = type.Name,
                        message = $"Component {type.Name} not found on GameObject"
                    };
                }

                // Check component index
                if (componentIndex >= components.Length)
                {
                    return new { error = $"Component index {componentIndex} out of range (found {components.Length} components)" };
                }

                // Remove the component
                Component componentToRemove = components[componentIndex];
                Undo.DestroyObjectImmediate(componentToRemove);

                return new
                {
                    success = true,
                    removed = true,
                    componentType = type.Name,
                    componentIndex = componentIndex,
                    message = $"Component {type.Name}[{componentIndex}] removed successfully"
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ComponentHandler] Error in RemoveComponent: {ex.Message}");
                return new { error = $"Failed to remove component: {ex.Message}" };
            }
        }

        /// <summary>
        /// Modifies properties of an existing component
        /// </summary>
        public static object ModifyComponent(JObject parameters)
        {
            try
            {
                // Parse parameters
                string gameObjectPath = parameters["gameObjectPath"]?.ToString();
                string componentType = parameters["componentType"]?.ToString();
                int componentIndex = parameters["componentIndex"]?.ToObject<int>() ?? 0;
                JObject properties = parameters["properties"] as JObject;

                // Validate parameters
                if (string.IsNullOrEmpty(gameObjectPath))
                {
                    return new { error = "gameObjectPath is required" };
                }

                if (string.IsNullOrEmpty(componentType))
                {
                    return new { error = "componentType is required" };
                }

                if (properties == null || !properties.HasValues)
                {
                    return new { error = "properties is required and cannot be empty" };
                }

                // Find GameObject
                GameObject targetObject = GameObject.Find(gameObjectPath);
                if (targetObject == null)
                {
                    return new { error = $"GameObject not found: {gameObjectPath}" };
                }

                // Resolve component type
                Type type = ResolveComponentType(componentType);
                if (type == null)
                {
                    return new { error = $"Component type not found: {componentType}" };
                }

                // Get component
                Component[] components = targetObject.GetComponents(type);
                if (components.Length == 0)
                {
                    return new { error = $"Component {type.Name} not found on GameObject" };
                }

                if (componentIndex >= components.Length)
                {
                    return new { error = $"Component index {componentIndex} out of range" };
                }

                Component component = components[componentIndex];

                // Record undo
                Undo.RecordObject(component, $"Modify {type.Name}");

                // Apply properties
                var modifiedProperties = new List<string>();
                foreach (var prop in properties.Properties())
                {
                    if (SetComponentProperty(component, prop.Name, prop.Value))
                    {
                        modifiedProperties.Add(prop.Name);
                    }
                    else
                    {
                        // Try to provide helpful error for first failed property
                        if (modifiedProperties.Count == 0)
                        {
                            return new { error = $"Property not found or invalid: {prop.Name}" };
                        }
                    }
                }

                // Mark as dirty for saving
                EditorUtility.SetDirty(component);

                return new
                {
                    success = true,
                    componentType = type.Name,
                    componentIndex = componentIndex,
                    modifiedProperties = modifiedProperties.ToArray(),
                    message = $"Component {type.Name} properties updated"
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ComponentHandler] Error in ModifyComponent: {ex.Message}");
                return new { error = $"Failed to modify component: {ex.Message}" };
            }
        }

        /// <summary>
        /// Lists all components on a GameObject
        /// </summary>
        public static object ListComponents(JObject parameters)
        {
            try
            {
                // Parse parameters
                string gameObjectPath = parameters["gameObjectPath"]?.ToString();
                bool includeProperties = parameters["includeProperties"]?.ToObject<bool>() ?? false;

                // Validate parameters
                if (string.IsNullOrEmpty(gameObjectPath))
                {
                    return new { error = "gameObjectPath is required" };
                }

                // Find GameObject
                GameObject targetObject = GameObject.Find(gameObjectPath);
                if (targetObject == null)
                {
                    return new { error = $"GameObject not found: {gameObjectPath}" };
                }

                // Get all components
                Component[] components = targetObject.GetComponents<Component>();
                var componentList = new List<object>();

                foreach (var component in components)
                {
                    if (component == null) continue;

                    var componentInfo = new Dictionary<string, object>
                    {
                        ["type"] = component.GetType().Name,
                        ["enabled"] = IsComponentEnabled(component)
                    };

                    // Include properties if requested
                    if (includeProperties)
                    {
                        var properties = GetComponentProperties(component);
                        if (properties.Count > 0)
                        {
                            componentInfo["properties"] = properties;
                        }
                    }

                    componentList.Add(componentInfo);
                }

                return new
                {
                    success = true,
                    gameObjectPath = gameObjectPath,
                    components = componentList,
                    componentCount = componentList.Count,
                    message = $"Found {componentList.Count} components"
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ComponentHandler] Error in ListComponents: {ex.Message}");
                return new { error = $"Failed to list components: {ex.Message}" };
            }
        }

        /// <summary>
        /// Lists available component types (concrete public UnityEngine.Component
        /// subclasses) with optional name search, namespace-based category filter, and
        /// an addable-only (non-abstract) filter. Written natively to Core's
        /// HandlerOutcome contract and served via the CommandDispatcher rail.
        /// </summary>
        public static HandlerOutcome GetComponentTypes(JObject parameters)
        {
            try
            {
                string category = parameters?["category"]?.ToString();
                string search = parameters?["search"]?.ToString();
                bool onlyAddable = parameters?["onlyAddable"]?.ToObject<bool>() ?? false;

                var baseType = typeof(Component);
                var all = new List<Type>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null || !t.IsClass || !t.IsVisible) continue;
                        if (t.IsGenericTypeDefinition) continue;
                        if (!baseType.IsAssignableFrom(t)) continue;
                        all.Add(t);
                    }
                }

                IEnumerable<Type> filtered = all;
                if (onlyAddable)
                    filtered = filtered.Where(t => !t.IsAbstract && t != typeof(Transform));
                if (!string.IsNullOrEmpty(search))
                    filtered = filtered.Where(t => t.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrEmpty(category))
                    filtered = filtered.Where(t => (t.Namespace ?? "").IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0);

                var list = filtered
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .Select(t => new
                    {
                        name = t.Name,
                        fullName = t.FullName,
                        @namespace = t.Namespace ?? "",
                        isAbstract = t.IsAbstract
                    })
                    .ToList();

                var categories = all
                    .Select(t => t.Namespace ?? "")
                    .Distinct()
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();

                return HandlerOutcome.Ok(new
                {
                    componentTypes = list,
                    totalCount = list.Count,
                    categories,
                    searchTerm = search,
                    onlyAddable
                });
            }
            catch (Exception ex)
            {
                return HandlerOutcome.Fail($"Failed to get component types: {ex.Message}", "INTERNAL_ERROR");
            }
        }

        #region Helper Methods

        /// <summary>
        /// Resolves a component type from string name
        /// </summary>
        public static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            // First try exact type name
            Type type = Type.GetType(typeName);
            if (type != null && typeof(Component).IsAssignableFrom(type))
                return type;

            // Try with UnityEngine namespace
            type = Type.GetType($"UnityEngine.{typeName}, UnityEngine");
            if (type != null && typeof(Component).IsAssignableFrom(type))
                return type;

            // Try with UnityEngine.UI namespace
            type = Type.GetType($"UnityEngine.UI.{typeName}, UnityEngine.UI");
            if (type != null && typeof(Component).IsAssignableFrom(type))
                return type;

            // Search all loaded assemblies by short OR fully-qualified name, so
            // namespaced inputs like "UnityEngine.Rigidbody" resolve too (a bare
            // full name has no assembly hint for Type.GetType above).
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // GetTypes() throws ReflectionTypeLoadException on assemblies with
                // unresolvable references; recover the types that DID load (mirrors the
                // guarded enumeration in GetComponentTypes) so resolution doesn't fail
                // project-wide because one plugin assembly is half-broken.
                Type[] asmTypes;
                try { asmTypes = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { asmTypes = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                type = asmTypes.FirstOrDefault(t =>
                    (t.Name == typeName || t.FullName == typeName) &&
                    typeof(Component).IsAssignableFrom(t));

                if (type != null)
                    return type;
            }

            return null;
        }

        /// <summary>
        /// Checks if a component type allows only one instance per GameObject
        /// </summary>
        private static bool IsUniqueComponent(Type type)
        {
            // Most components can have multiple instances
            // These are the common unique ones:
            return type == typeof(Transform) ||
                   type == typeof(RectTransform) ||
                   type == typeof(Rigidbody) ||
                   type == typeof(Rigidbody2D) ||
                   type == typeof(Animator) ||
                   type == typeof(AudioListener);
        }

        /// <summary>
        /// Sets a property value on a component
        /// </summary>
        private static bool SetComponentProperty(Component component, string propertyName, JToken value)
        {
            try
            {
                if (TrySetRigidbodyCompatibilityProperty(component, propertyName, value))
                {
                    return true;
                }

                Type type = component.GetType();
                
                // Try field first
                FieldInfo field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    object convertedValue = ConvertValue(value, field.FieldType);
                    field.SetValue(component, convertedValue);
                    return true;
                }

                // Try property
                PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite)
                {
                    object convertedValue = ConvertValue(value, property.PropertyType);
                    property.SetValue(component, convertedValue);
                    return true;
                }

                // Handle nested properties (e.g., "constraints.freezePositionX")
                if (propertyName.Contains("."))
                {
                    return SetNestedProperty(component, propertyName, value);
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to set property {propertyName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets a nested property value
        /// </summary>
        private static bool SetNestedProperty(Component component, string propertyPath, JToken value)
        {
            string[] parts = propertyPath.Split('.');

            // Record the navigation chain so value-type (struct) intermediates are written back: a
            // struct read via reflection is a boxed COPY, so setting the final field on it is silently
            // lost unless the copy is propagated back up to its owner (e.g. transform.localPosition.x).
            // (Audit finding #37.)
            var chain = new List<(object owner, MemberInfo member)>();
            object current = component;
            Type currentType = component.GetType();

            for (int i = 0; i < parts.Length - 1; i++)
            {
                MemberInfo member = (MemberInfo)currentType.GetField(parts[i], BindingFlags.Public | BindingFlags.Instance)
                    ?? currentType.GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance);
                if (member == null) return false;
                chain.Add((current, member));
                current = GetMemberValue(member, current);
                if (current == null) return false;
                currentType = current.GetType();
            }

            // Set the final field/property on the deepest (possibly boxed) object.
            string finalProp = parts[parts.Length - 1];
            MemberInfo finalMember = (MemberInfo)currentType.GetField(finalProp, BindingFlags.Public | BindingFlags.Instance)
                ?? currentType.GetProperty(finalProp, BindingFlags.Public | BindingFlags.Instance);
            if (finalMember == null || !CanWriteMember(finalMember)) return false;
            SetMemberValue(finalMember, current, ConvertValue(value, MemberValueType(finalMember)));

            // Propagate the (possibly mutated) boxed value back up the chain. Reference-type
            // intermediates were already mutated in place; struct intermediates MUST be written back.
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var (owner, member) = chain[i];
                if (!CanWriteMember(member))
                {
                    // A get-only struct intermediate can't receive the mutated copy -> the set won't stick.
                    if (MemberValueType(member).IsValueType) return false;
                    break; // reference type already mutated in place
                }
                SetMemberValue(member, owner, current);
                current = owner;
            }
            return true;
        }

        private static object GetMemberValue(MemberInfo m, object obj) =>
            m is FieldInfo f ? f.GetValue(obj) : ((PropertyInfo)m).GetValue(obj);

        private static void SetMemberValue(MemberInfo m, object obj, object val)
        {
            if (m is FieldInfo f) f.SetValue(obj, val);
            else ((PropertyInfo)m).SetValue(obj, val);
        }

        private static Type MemberValueType(MemberInfo m) =>
            m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;

        private static bool CanWriteMember(MemberInfo m) =>
            m is FieldInfo f ? !f.IsInitOnly : ((PropertyInfo)m).CanWrite;

        /// <summary>
        /// Preserves MCP Rigidbody field names across Unity versions.
        /// </summary>
        private static bool TrySetRigidbodyCompatibilityProperty(Component component, string propertyName, JToken value)
        {
            if (!(component is Rigidbody rb))
            {
                return false;
            }

            if (propertyName == "drag" || propertyName == "linearDamping")
            {
                var convertedValue = (float)ConvertValue(value, typeof(float));
#if UNITY_6000_0_OR_NEWER
                rb.linearDamping = convertedValue;
#else
                rb.drag = convertedValue;
#endif
                return true;
            }

            if (propertyName == "angularDrag" || propertyName == "angularDamping")
            {
                var convertedValue = (float)ConvertValue(value, typeof(float));
#if UNITY_6000_0_OR_NEWER
                rb.angularDamping = convertedValue;
#else
                rb.angularDrag = convertedValue;
#endif
                return true;
            }

            return false;
        }

        /// <summary>
        /// Converts a JSON value to the target type
        /// </summary>
        public static object ConvertValue(JToken value, Type targetType)
        {
            if (value == null || value.Type == JTokenType.Null)
                return null;

            // Handle Unity-specific types
            if (targetType == typeof(Vector3))
            {
                if (value.Type == JTokenType.Object)
                {
                    float x = value["x"]?.ToObject<float>() ?? 0f;
                    float y = value["y"]?.ToObject<float>() ?? 0f;
                    float z = value["z"]?.ToObject<float>() ?? 0f;
                    return new Vector3(x, y, z);
                }
            }
            else if (targetType == typeof(Vector2))
            {
                if (value.Type == JTokenType.Object)
                {
                    float x = value["x"]?.ToObject<float>() ?? 0f;
                    float y = value["y"]?.ToObject<float>() ?? 0f;
                    return new Vector2(x, y);
                }
            }
            else if (targetType == typeof(Color))
            {
                if (value.Type == JTokenType.Object)
                {
                    float r = value["r"]?.ToObject<float>() ?? 0f;
                    float g = value["g"]?.ToObject<float>() ?? 0f;
                    float b = value["b"]?.ToObject<float>() ?? 0f;
                    float a = value["a"]?.ToObject<float>() ?? 1f;
                    return new Color(r, g, b, a);
                }
            }
            else if (targetType == typeof(Quaternion))
            {
                if (value.Type == JTokenType.Object)
                {
                    float x = value["x"]?.ToObject<float>() ?? 0f;
                    float y = value["y"]?.ToObject<float>() ?? 0f;
                    float z = value["z"]?.ToObject<float>() ?? 0f;
                    float w = value["w"]?.ToObject<float>() ?? 1f;
                    return new Quaternion(x, y, z, w);
                }
            }
            else if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, value.ToString(), true);
            }

            // Use JSON.NET for other conversions
            try
            {
                return value.ToObject(targetType);
            }
            catch
            {
                // Fallback to basic conversion
                return Convert.ChangeType(value.ToString(), targetType);
            }
        }

        /// <summary>
        /// Checks if a component is enabled
        /// </summary>
        private static bool IsComponentEnabled(Component component)
        {
            // Handle Behaviour components (most Unity components)
            if (component is Behaviour behaviour)
                return behaviour.enabled;

            // Handle Renderer
            if (component is Renderer renderer)
                return renderer.enabled;

            // Handle Collider
            if (component is Collider collider)
                return collider.enabled;

            // Default to true for other components
            return true;
        }

        /// <summary>
        /// Gets properties of a component
        /// </summary>
        private static Dictionary<string, object> GetComponentProperties(Component component)
        {
            var properties = new Dictionary<string, object>();
            Type type = component.GetType();

            // Get common properties based on component type
            switch (component)
            {
                case Transform transform:
                    properties["position"] = new { x = transform.position.x, y = transform.position.y, z = transform.position.z };
                    properties["rotation"] = new { x = transform.eulerAngles.x, y = transform.eulerAngles.y, z = transform.eulerAngles.z };
                    properties["scale"] = new { x = transform.localScale.x, y = transform.localScale.y, z = transform.localScale.z };
                    break;

                case Rigidbody rb:
                    properties["mass"] = rb.mass;
#if UNITY_6000_0_OR_NEWER
                    properties["drag"] = rb.linearDamping;
                    properties["angularDrag"] = rb.angularDamping;
#else
                    properties["drag"] = rb.drag;
                    properties["angularDrag"] = rb.angularDrag;
#endif
                    properties["useGravity"] = rb.useGravity;
                    properties["isKinematic"] = rb.isKinematic;
                    break;

                case BoxCollider box:
                    properties["isTrigger"] = box.isTrigger;
                    properties["center"] = new { x = box.center.x, y = box.center.y, z = box.center.z };
                    properties["size"] = new { x = box.size.x, y = box.size.y, z = box.size.z };
                    break;

                case Light light:
                    properties["type"] = light.type.ToString();
                    properties["color"] = new { r = light.color.r, g = light.color.g, b = light.color.b, a = light.color.a };
                    properties["intensity"] = light.intensity;
                    properties["range"] = light.range;
                    break;

                case Camera camera:
                    properties["fieldOfView"] = camera.fieldOfView;
                    properties["nearClipPlane"] = camera.nearClipPlane;
                    properties["farClipPlane"] = camera.farClipPlane;
                    properties["depth"] = camera.depth;
                    break;

                default:
                    // For other components, get first few public properties
                    var publicProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    int count = 0;
                    foreach (var prop in publicProperties.Where(p => p.CanRead).Take(10))
                    {
                        try
                        {
                            var value = prop.GetValue(component);
                            if (value != null && IsSerializableValue(value))
                            {
                                properties[prop.Name] = SerializeValue(value);
                                count++;
                                if (count >= 5) break; // Limit to 5 properties
                            }
                        }
                        catch { }
                    }
                    break;
            }

            return properties;
        }

        /// <summary>
        /// Checks if a value can be serialized
        /// </summary>
        private static bool IsSerializableValue(object value)
        {
            Type type = value.GetType();
            return type.IsPrimitive || 
                   type == typeof(string) || 
                   type == typeof(Vector3) || 
                   type == typeof(Vector2) ||
                   type == typeof(Color) ||
                   type == typeof(Quaternion);
        }

        /// <summary>
        /// Serializes a value for JSON
        /// </summary>
        private static object SerializeValue(object value)
        {
            if (value is Vector3 v3)
                return new { x = v3.x, y = v3.y, z = v3.z };
            if (value is Vector2 v2)
                return new { x = v2.x, y = v2.y };
            if (value is Color c)
                return new { r = c.r, g = c.g, b = c.b, a = c.a };
            if (value is Quaternion q)
                return new { x = q.x, y = q.y, z = q.z, w = q.w };
            return value;
        }

        #endregion
    }
}
