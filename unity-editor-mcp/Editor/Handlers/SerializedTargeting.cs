using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Handlers
{
    public struct ResolvedTarget { public Object Obj; public Component Component; public string Describe; }

    public static class SerializedTargeting
    {
        // Resolve a single target descriptor (+ optional component/componentIndex siblings) → the Object whose
        // SerializedObject we edit. Returns false + code/message on failure.
        public static bool ResolveSingle(JObject target, JObject parent, out ResolvedTarget result, out string code, out string message)
        {
            result = default; code = null; message = null;
            Object obj = null;
            if (target?["instanceId"] != null) obj = EditorUtility.InstanceIDToObject(target["instanceId"].Value<int>());
            else if (target?["assetPath"] != null) obj = AssetDatabase.LoadMainAssetAtPath(target["assetPath"].Value<string>());
            else if (target?["guid"] != null) obj = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(target["guid"].Value<string>()));
            else if (target?["scenePath"] != null) obj = FindByScenePath(target["scenePath"].Value<string>());
            else { code = "VALIDATION_ERROR"; message = "target needs one of instanceId/assetPath/guid/scenePath"; return false; }

            if (obj == null) { code = "TARGET_NOT_FOUND"; message = "target did not resolve"; return false; }

            // If the resolved object is a GameObject and a component is named, narrow to that component.
            var compName = parent?["component"]?.Value<string>();
            if (obj is GameObject go && compName != null)
            {
                var idx = parent?["componentIndex"]?.Value<int>() ?? 0;
                var comp = go.GetComponents<Component>().Where(c => c != null && (c.GetType().Name == compName || c.GetType().FullName == compName)).Skip(idx).FirstOrDefault();
                if (comp == null) { code = "COMPONENT_NOT_FOUND"; message = $"component {compName}[{idx}] not found"; return false; }
                result = new ResolvedTarget { Obj = comp, Component = comp, Describe = $"{ScenePath(go)}::{compName}[{idx}]" };
                return true;
            }
            var ap = AssetDatabase.GetAssetPath(obj);
            result = new ResolvedTarget { Obj = obj, Component = obj as Component, Describe = !string.IsNullOrEmpty(ap) ? ap : obj.name };
            return true;
        }

        public static List<ResolvedTarget> ResolveMatch(JObject match, JObject parent, int max, out string code, out string message)
        {
            code = null; message = null;
            var list = new List<ResolvedTarget>();
            IEnumerable<GameObject> roots = LoadedRootObjects();
            IEnumerable<GameObject> gos;
            if (match?["scenePaths"] is JArray paths) gos = paths.Select(p => FindByScenePath(p.Value<string>())).Where(g => g != null);
            else if (match?["selection"]?.Value<bool>() == true) gos = Selection.gameObjects;
            else if (match?["tag"] != null) gos = AllGameObjects(roots).Where(g => g.CompareTag(match["tag"].Value<string>()));
            else if (match?["componentType"] != null) { var ct = match["componentType"].Value<string>(); gos = AllGameObjects(roots).Where(g => g.GetComponents<Component>().Any(c => c != null && (c.GetType().Name == ct || c.GetType().FullName == ct))); }
            else if (match?["prefab"] != null) { var src = LoadPrefab(match["prefab"].Value<string>()); gos = AllGameObjects(roots).Where(g => MatchesPrefab(g, src)); }
            else { code = "VALIDATION_ERROR"; message = "match needs one of prefab/componentType/tag/selection/scenePaths"; return list; }

            // round-7 BUG 1: a componentType match must edit that COMPONENT's SerializedObject, not the GameObject's
            // (else the component's properties read as PROPERTY_NOT_FOUND). Narrow to the component automatically — the
            // caller shouldn't have to ALSO pass `component`.
            var matchComp = match?["componentType"]?.Value<string>();
            JObject effParent = parent;
            if (matchComp != null && parent?["component"] == null)
            {
                effParent = parent != null ? (JObject)parent.DeepClone() : new JObject();
                effParent["component"] = matchComp;
            }
            foreach (var go in gos.Distinct())
            {
                if (list.Count >= max) break;
                if (ResolveSingle(new JObject { ["instanceId"] = go.GetInstanceID() }, effParent, out var rt, out _, out _)) list.Add(rt);
            }
            return list;
        }

        public static Object ResolveObjectReference(JObject v, out string error)
        {
            error = null;
            if (v == null) return null;
            if (v["instanceId"] != null) return EditorUtility.InstanceIDToObject(v["instanceId"].Value<int>());
            if (v["guid"] != null) return AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(v["guid"].Value<string>()));
            if (v["assetPath"] != null) return AssetDatabase.LoadMainAssetAtPath(v["assetPath"].Value<string>());
            if (v["scenePath"] != null) return FindByScenePath(v["scenePath"].Value<string>());
            error = "TYPE_MISMATCH: object reference needs instanceId/guid/assetPath/scenePath or null";
            return null;
        }

        private static bool MatchesPrefab(GameObject g, GameObject src)
        {
            var s = PrefabUtility.GetCorrespondingObjectFromSource(g) as GameObject;
            if (s == null) return false;
            if (src == null) return true; // any prefab instance
            return s == src || PrefabUtility.GetCorrespondingObjectFromSource(s) == src;
        }

        private static GameObject LoadPrefab(string s) => s != null && s.StartsWith("Assets/") ? AssetDatabase.LoadAssetAtPath<GameObject>(s) : AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(s));
        private static IEnumerable<GameObject> LoadedRootObjects()
        {
            // round-8: include the open prefab stage's roots so the serialization tools (inspect/set via scenePath or
            // match) reach stage objects — otherwise they silently target the background main scene while a prefab is
            // open. Stage first (matches the by-path resolvers' stage-first preference).
            var stage = AssetManagementHandler.GetOpenPrefabStageScene();
            if (stage.HasValue && stage.Value.IsValid())
                foreach (var r in stage.Value.GetRootGameObjects()) yield return r;
            for (int i = 0; i < SceneManager.sceneCount; i++) { var sc = SceneManager.GetSceneAt(i); if (sc.isLoaded) foreach (var r in sc.GetRootGameObjects()) yield return r; }
        }
        private static IEnumerable<GameObject> AllGameObjects(IEnumerable<GameObject> roots) { foreach (var r in roots) foreach (var t in r.GetComponentsInChildren<Transform>(true)) yield return t.gameObject; }
        private static GameObject FindByScenePath(string path) => AllGameObjects(LoadedRootObjects()).FirstOrDefault(g => ScenePath(g) == path);
        public static string ScenePath(GameObject go) { var s = "/" + go.name; var t = go.transform; while (t.parent != null) { t = t.parent; s = "/" + t.name + s; } return s; }
    }
}
