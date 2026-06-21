using System;
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
            var fieldConstraint = p.managedReferenceFieldTypename;
            var constraint = ResolveType(fieldConstraint);
            // A DECLARED constraint we cannot resolve must FAIL — never silently accept any type (the agent should
            // supply a full name). Only a genuinely unconstrained field (empty constraint) skips the check.
            if (!string.IsNullOrEmpty(fieldConstraint) && constraint == null)
            { error = $"CONSTRAINT_NOT_RESOLVED: cannot resolve the field's managed-reference constraint '{fieldConstraint}'"; return false; }
            if (constraint != null && !constraint.IsAssignableFrom(type)) { error = $"TYPE_NOT_ASSIGNABLE: {type.FullName} is not assignable to {constraint.FullName}"; return false; }
            object instance;
            try { instance = Activator.CreateInstance(type); }                       // run a parameterless ctor for defaults
            catch { try { instance = FormatterServices.GetUninitializedObject(type); } // fallback: no ctor (Unity's path)
                    catch (Exception e) { error = $"cannot instantiate {type.FullName}: {e.Message}"; return false; } }
            p.managedReferenceValue = instance;
            return true;
        }

        // Resolve a type by FullName or AQN, OR the "AssemblyName FullName" managedReference*Typename format.
        // Requires a FullName (the agent obtains it from inspect / find_implementations) — never a bare simple
        // name, which collides across the editor's many assemblies and would pick a type non-deterministically.
        public static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            // The assembly name has no space, so the FIRST space splits "AssemblyName FullName"; the remainder is
            // the FullName (which may itself contain spaces for generics). A plain FullName/AQN passes through.
            var full = name.Contains(" ") ? name.Substring(name.IndexOf(' ') + 1) : name;
            var t = Type.GetType(full);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(full); } catch { t = null; } // exact FullName per assembly — no simple-name scan
                if (t != null) return t;
            }
            return null;
        }
    }
}
