using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath tests for the Analysis tool category (SceneAnalysisHandler). Each test builds a KNOWN
    /// fixture of GameObjects/components in the live scene, calls the handler, then INDEPENDENTLY re-reads
    /// the SAME ground truth through a DIFFERENT path (the raw UnityEngine objects + a hand-rolled count
    /// over Resources.FindObjectsOfTypeAll / scene roots) and asserts the handler's reported data matches
    /// that independent measurement AND that an unrelated baseline did not move. Every test destroys
    /// everything it created (Object.DestroyImmediate) so it leaves zero residue.
    ///
    /// Why deltas (not absolute counts) for analyze_scene_contents: AnalyzeSceneContents counts EVERY
    /// GameObject across ALL loaded scenes (the Test Runner's own scene context is present too), so the
    /// fixture's contribution is asserted as a measured DELTA against a baseline captured immediately
    /// before the fixture is built and re-measured the SAME way the handler does.
    /// </summary>
    public class AftermathTests_Analysis
    {
        // ---------------------------------------------------------------------------------------------
        // analyze_scene_contents
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void AnalyzeSceneContents_CountsAndComponentDistribution_MatchKnownFixture()
        {
            // Baseline: count GameObjects + scene roots EXACTLY the way the handler does (default
            // includeInactive=true), BEFORE the fixture exists, so we can assert our fixture's delta.
            int baselineTotal = CountAllLoadedGameObjects();
            int baselineRoots = CountAllSceneRoots();

            // Independent baseline for the component type we will introduce a known number of: count the
            // SphereCollider instances already present so the assertion is a delta, not an absolute.
            int baselineSphereColliders = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(go => go.scene.isLoaded)
                .SelectMany(go => go.GetComponents<SphereCollider>())
                .Count(c => c != null);

            GameObject root = null;
            try
            {
                // Arrange a KNOWN fixture: 1 root + 2 children = 3 new GameObjects.
                // Components we control: 1 BoxCollider (on root), 2 SphereColliders (one per child),
                // plus 1 Light (on a child). Transforms count as components too but we assert by the
                // distinct collider/light types we introduced.
                root = new GameObject("AftermathAnalyzeRoot");
                root.AddComponent<BoxCollider>();

                var childA = new GameObject("AftermathAnalyzeChildA");
                childA.transform.SetParent(root.transform);
                childA.AddComponent<SphereCollider>();

                var childB = new GameObject("AftermathAnalyzeChildB");
                childB.transform.SetParent(root.transform);
                childB.AddComponent<SphereCollider>();
                var light = childB.AddComponent<Light>();
                light.type = LightType.Point;

                // Independent re-measurement (same method the handler uses), AFTER building the fixture.
                int expectedTotal = CountAllLoadedGameObjects();
                int expectedRoots = CountAllSceneRoots();
                Assert.AreEqual(baselineTotal + 3, expectedTotal, "fixture should add exactly 3 GameObjects");
                Assert.AreEqual(baselineRoots + 1, expectedRoots, "fixture should add exactly 1 root object");

                // Act
                var r = SceneAnalysisHandler.AnalyzeSceneContents(new JObject());

                // Assert: handler succeeded.
                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);

                // AFTERMATH (independent path #1 — statistics vs hand-rolled scene scan):
                var stats = (JObject)payload["statistics"];
                Assert.IsNotNull(stats, "statistics object should be present");
                Assert.AreEqual(expectedTotal, (int)stats["totalGameObjects"],
                    "totalGameObjects must equal the independent all-loaded-scenes count");
                Assert.AreEqual(expectedRoots, (int)stats["rootObjects"],
                    "rootObjects must equal the independent sum of GetRootGameObjects across loaded scenes");

                // AFTERMATH (independent path #2 — componentDistribution vs the components we attached):
                // The handler buckets non-MonoBehaviour components by GetType().Name. Our fixture added
                // exactly +1 BoxCollider, +2 SphereCollider over the baseline.
                var dist = (JObject)payload["componentDistribution"];
                Assert.IsNotNull(dist, "componentDistribution should be present");

                int reportedSphere = dist["SphereCollider"] != null ? (int)dist["SphereCollider"] : 0;
                Assert.AreEqual(baselineSphereColliders + 2, reportedSphere,
                    "SphereCollider distribution should reflect the 2 we added");

                Assert.IsNotNull(dist["BoxCollider"], "BoxCollider must appear in the distribution");
                Assert.GreaterOrEqual((int)dist["BoxCollider"], 1, "at least the 1 BoxCollider we added");

                // Cross-check the SphereCollider count the handler reported against an INDEPENDENT live count.
                int liveSphere = Resources.FindObjectsOfTypeAll<GameObject>()
                    .Where(go => go.scene.isLoaded)
                    .SelectMany(go => go.GetComponents<SphereCollider>())
                    .Count(c => c != null);
                Assert.AreEqual(liveSphere, reportedSphere,
                    "reported SphereCollider count must equal the live SphereCollider count");
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
            }

            // Baseline restored: after destroying the fixture, the all-loaded count returns to baseline.
            Assert.AreEqual(baselineTotal, CountAllLoadedGameObjects(),
                "GameObject count must return to baseline after cleanup (zero residue)");
            Assert.AreEqual(baselineRoots, CountAllSceneRoots(),
                "root count must return to baseline after cleanup (zero residue)");
        }

        // ---------------------------------------------------------------------------------------------
        // get_gameobject_details
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void GetGameObjectDetails_TransformTagLayerComponentsChildren_MatchRawObject()
        {
            GameObject root = null;
            try
            {
                // Arrange a KNOWN object with a DISTINCT local transform, a known tag/layer, known
                // components, and one child. Tag "MainCamera" and layer "UI" are built-in (always exist),
                // so this needs no settings mutation.
                root = new GameObject("AftermathDetailsRoot");
                root.tag = "MainCamera";              // built-in tag, no TagManager mutation
                root.layer = LayerMask.NameToLayer("UI"); // built-in layer index 5
                root.transform.localPosition = new Vector3(3f, -4f, 5.5f);
                root.transform.localEulerAngles = new Vector3(10f, 20f, 30f);
                root.transform.localScale = new Vector3(2f, 0.5f, 1.25f);
                root.AddComponent<BoxCollider>();
                var rb = root.AddComponent<Rigidbody>();
                rb.mass = 7f;

                var child = new GameObject("AftermathDetailsChild");
                child.transform.SetParent(root.transform);

                // Independent ground truth straight off the raw object.
                var rawTransform = root.transform;
                string rawTag = root.tag;
                string rawLayerName = LayerMask.LayerToName(root.layer);
                var rawComponentTypeNames = root.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToList();

                // Act
                var r = SceneAnalysisHandler.GetGameObjectDetails(new JObject
                {
                    ["gameObjectName"] = "AftermathDetailsRoot",
                    ["includeChildren"] = true,
                    ["includeComponents"] = true
                });

                // Assert: handler succeeded.
                Assert.IsFalse(r.IsError, r.Error);
                var details = JObject.FromObject(r.Payload);

                // AFTERMATH #1 — name/tag/layer vs the raw object.
                Assert.AreEqual(root.name, (string)details["name"], "name mismatch");
                Assert.AreEqual(rawTag, (string)details["tag"], "tag must match the raw GameObject.tag");
                Assert.AreEqual(rawLayerName, (string)details["layer"],
                    "layer must be reported as the raw layer NAME");

                // AFTERMATH #2 — transform (handler reports LOCAL position/rotation/scale) vs the raw transform.
                var tr = (JObject)details["transform"];
                Assert.IsNotNull(tr, "transform object should be present");
                AssertVec3(tr["position"], rawTransform.localPosition, "transform.position (local)");
                AssertVec3(tr["scale"], rawTransform.localScale, "transform.scale (local)");
                // worldPosition is also reported; with no parent offset it equals the raw world position.
                AssertVec3(tr["worldPosition"], rawTransform.position, "transform.worldPosition");

                // AFTERMATH #3 — components[] vs the raw component set. Every type the raw object has must be
                // present in the handler's components array, including the Transform, BoxCollider, Rigidbody.
                var comps = (JArray)details["components"];
                Assert.IsNotNull(comps, "components array should be present");
                var reportedTypes = comps.OfType<JObject>().Select(c => (string)c["type"]).ToList();
                foreach (var t in rawComponentTypeNames)
                {
                    Assert.Contains(t, reportedTypes,
                        "component type '" + t + "' present on the raw object is missing from the report");
                }
                Assert.AreEqual(rawComponentTypeNames.Count, reportedTypes.Count,
                    "reported component count must equal the raw component count");

                // Spot-check a serialized property: Rigidbody.mass we set to 7 must round-trip.
                var rbReport = comps.OfType<JObject>().FirstOrDefault(c => (string)c["type"] == "Rigidbody");
                Assert.IsNotNull(rbReport, "Rigidbody should appear in components");
                Assert.AreEqual(7f, (float)rbReport["properties"]["mass"], 0.0001f, "Rigidbody.mass should round-trip as 7 (within float precision)");

                // AFTERMATH #4 — children[] vs the raw hierarchy: exactly one child, by name.
                var children = (JArray)details["children"];
                Assert.IsNotNull(children, "children array should be present");
                Assert.AreEqual(root.transform.childCount, children.Count,
                    "reported child count must equal the raw transform.childCount");
                Assert.AreEqual("AftermathDetailsChild", (string)children[0]["name"], "child name mismatch");
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
            }

            // Zero residue: the named object is gone.
            Assert.IsNull(GameObject.Find("AftermathDetailsRoot"),
                "fixture GameObject must be destroyed after the test");
        }

        // ---------------------------------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Counts every GameObject across all loaded scenes the SAME way AnalyzeSceneContents does for its
        /// default includeInactive=true branch (Resources.FindObjectsOfTypeAll filtered to loaded scenes).
        /// </summary>
        private static int CountAllLoadedGameObjects()
        {
            return Resources.FindObjectsOfTypeAll<GameObject>()
                .Count(go => go.scene.isLoaded);
        }

        /// <summary>
        /// Sums GetRootGameObjects() across all loaded scenes — the handler's definition of rootObjects.
        /// </summary>
        private static int CountAllSceneRoots()
        {
            int roots = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) roots += s.GetRootGameObjects().Length;
            }
            return roots;
        }

        /// <summary>
        /// Asserts a serialized {x,y,z} JSON vector equals an expected Vector3 (per-component, tolerant).
        /// </summary>
        private static void AssertVec3(JToken token, Vector3 expected, string label)
        {
            Assert.IsNotNull(token, label + " should be present");
            Assert.AreEqual(expected.x, (float)token["x"], 1e-3f, label + ".x mismatch");
            Assert.AreEqual(expected.y, (float)token["y"], 1e-3f, label + ".y mismatch");
            Assert.AreEqual(expected.z, (float)token["z"], 1e-3f, label + ".z mismatch");
        }
    }
}
