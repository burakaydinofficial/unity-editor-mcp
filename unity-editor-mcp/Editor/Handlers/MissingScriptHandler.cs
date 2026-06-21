using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>Find + remove missing-script MonoBehaviours in the active scene (F5) — a legacy-project staple
    /// (a deleted/moved .cs leaves a dangling component). Thin wrappers over Unity's GameObjectUtility APIs.</summary>
    public static class MissingScriptHandler
    {
        public static HandlerOutcome FindMissingScripts(JObject p)
        {
            try
            {
                int limit = p["limit"]?.ToObject<int>() ?? 200;
                if (limit <= 0) limit = 200;

                var objects = new List<object>();
                int totalMissing = 0;
                foreach (var go in ActiveSceneObjects())
                {
                    int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                    if (count > 0)
                    {
                        totalMissing += count;
                        objects.Add(new { path = HierarchyPath(go), missingCount = count });
                    }
                }
                int totalObjects = objects.Count;
                bool truncated = totalObjects > limit;            // F2: cap the response on a badly-broken scene
                if (truncated) objects = objects.Take(limit).ToList();
                return HandlerOutcome.Ok(new
                {
                    objects = objects,
                    totalObjects = totalObjects,
                    totalMissing = totalMissing,
                    truncated = truncated,
                    limit = limit
                });
            }
            catch (Exception e) { return HandlerOutcome.Fail($"find_missing_scripts failed: {e.Message}"); }
        }

        public static HandlerOutcome RemoveMissingScripts(JObject p)
        {
            try
            {
                if (EditorApplication.isPlaying)
                    return HandlerOutcome.Fail("scene mutations refuse in play mode", "PLAY_MODE");

                string[] paths = p["gameObjectPaths"]?.ToObject<string[]>();
                var notFound = new List<string>();
                List<GameObject> targets;
                if (paths != null && paths.Length > 0)
                {
                    targets = new List<GameObject>();
                    foreach (var path in paths)
                    {
                        var go = GameObject.Find(path);
                        if (go != null) targets.Add(go); else notFound.Add(path);
                    }
                }
                else
                {
                    targets = ActiveSceneObjects().ToList();
                }

                int removed = 0, objectsAffected = 0;
                foreach (var go in targets)
                {
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) <= 0) continue;
                    Undo.RegisterCompleteObjectUndo(go, "Remove Missing Scripts");
                    int r = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                    removed += r;
                    if (r > 0) objectsAffected++;
                }
                if (removed > 0) EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return HandlerOutcome.Ok(new
                {
                    removed = removed,
                    objectsAffected = objectsAffected,
                    notFound = notFound,
                    notFoundCount = notFound.Count
                });
            }
            catch (Exception e) { return HandlerOutcome.Fail($"remove_missing_scripts failed: {e.Message}"); }
        }

        private static IEnumerable<GameObject> ActiveSceneObjects()
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) // includes inactive
                    yield return t.gameObject;
        }

        private static string HierarchyPath(GameObject go)
        {
            var sb = new StringBuilder("/" + go.name);
            var parent = go.transform.parent;
            while (parent != null) { sb.Insert(0, "/" + parent.name); parent = parent.parent; }
            return sb.ToString();
        }
    }
}
