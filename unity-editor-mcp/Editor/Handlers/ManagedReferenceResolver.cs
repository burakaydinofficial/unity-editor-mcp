using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    /// <summary>Resolves + instantiates a concrete type for a [SerializeReference] field, validated assignable
    /// to the field constraint (managedReferenceFieldTypename). The agent discovers assignable concrete types
    /// via the existing find_implementations (0.6 lite layer) — it never guesses the $type.</summary>
    public static class ManagedReferenceResolver
    {
        // value: {"$type":"<FullName or AQN>"} to instantiate + assign, or JSON null to clear.
        public static bool TrySet(SerializedProperty p, JToken v, out string error)
        {
            error = null;
            if (v == null || v.Type == JTokenType.Null) { p.managedReferenceValue = null; return true; } // clear
            var typeName = (v as JObject)?["$type"]?.ToString();
            if (string.IsNullOrEmpty(typeName)) { error = "managed reference value needs {\"$type\":\"<name>\"} or null"; return false; }
            var type = ResolveType(typeName);
            if (type == null) { error = $"TYPE_NOT_FOUND: type not found: {typeName}"; return false; }
            var constraint = ResolveType(p.managedReferenceFieldTypename);
            if (constraint != null && !constraint.IsAssignableFrom(type)) { error = $"TYPE_NOT_ASSIGNABLE: {type.FullName} is not assignable to {constraint.FullName}"; return false; }
            object instance;
            try { instance = Activator.CreateInstance(type); }                       // run a parameterless ctor for defaults
            catch { try { instance = FormatterServices.GetUninitializedObject(type); } // fallback: no ctor (Unity's path)
                    catch (Exception e) { error = $"cannot instantiate {type.FullName}: {e.Message}"; return false; } }
            p.managedReferenceValue = instance;
            return true;
        }

        // Resolve from a plain FullName/simple name, OR the "AssemblyName FullName" managedReference*Typename format.
        public static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var full = name.Contains(" ") ? name.Substring(name.LastIndexOf(' ') + 1) : name;
            var t = Type.GetType(full);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(full); } catch { t = null; }
                if (t != null) return t;
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var c in SafeTypes(asm))
                    if (c.FullName == full || c.Name == full) return c;
            return null;
        }

        private static Type[] SafeTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null).ToArray(); }
            catch { return Array.Empty<Type>(); }
        }
    }
}
