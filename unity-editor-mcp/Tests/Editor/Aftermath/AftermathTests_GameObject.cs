using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath tests for the GameObject category. Each test calls a tool HANDLER, then INDEPENDENTLY
    /// re-reads Unity state through a DIFFERENT path (raw GameObject.Find / the sibling find_gameobject
    /// read handler) to assert the REAL effect happened AND that unrelated state did NOT change — not
    /// merely that the call did not error. Everything created is destroyed in a finally so no residue
    /// remains. C# 7.3 / netstandard 2.0 (2019.4 floor) — no C# 8+ syntax, floor-safe APIs only.
    ///
    /// SKIPPED in this category (see summary): remove_missing_scripts (positive path) — a true
    /// dangling/missing-script MonoBehaviour cannot be fabricated in EditMode without a script asset
    /// (GameObjectUtility owns that state); the empty-effect path is covered as a no-residue assertion.
    /// </summary>
    public class AftermathTests_GameObject
    {
        // --- delete_gameobject : SUCCESSFUL delete of a single object -------------------------------

        [Test]
        public void DeleteGameObject_Single_RemovesTargetAndLeavesSiblings()
        {
            // Arrange: a target to delete + an unrelated sibling that must SURVIVE (baseline).
            var target = new GameObject("AfterMath_DeleteTarget");
            var sibling = new GameObject("AfterMath_DeleteSibling");
            try
            {
                // Pre-condition: both resolvable via the independent raw API before the call.
                Assert.IsNotNull(GameObject.Find("/AfterMath_DeleteTarget"), "precondition: target should exist");
                Assert.IsNotNull(GameObject.Find("/AfterMath_DeleteSibling"), "precondition: sibling should exist");

                // Act
                var r = GameObjectHandler.DeleteGameObject(new JObject
                {
                    ["path"] = "/AfterMath_DeleteTarget"
                });

                // Assert outcome
                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);
                Assert.AreEqual(1, (int)payload["deletedCount"]);
                Assert.AreEqual(0, (int)payload["notFoundCount"]);

                // AFTERMATH via a DIFFERENT path (raw Unity API, not the handler):
                // the target is GONE and the unrelated sibling did NOT move.
                Assert.IsNull(GameObject.Find("/AfterMath_DeleteTarget"), "target should be gone after delete");
                Assert.IsNotNull(GameObject.Find("/AfterMath_DeleteSibling"), "sibling must survive an unrelated delete");

                // The handler's Undo.DestroyObjectImmediate detaches the object; null it so finally is a no-op.
                target = null;
            }
            finally
            {
                if (target != null) Object.DestroyImmediate(target);
                if (sibling != null) Object.DestroyImmediate(sibling);
            }
        }

        // --- delete_gameobject : SUCCESSFUL delete verified through the SIBLING read handler ---------

        [Test]
        public void DeleteGameObject_Verified_ViaFindGameObjectSiblingHandler()
        {
            var target = new GameObject("AfterMath_DeleteFindMe");
            try
            {
                // Pre-condition through the sibling READ handler: find resolves exactly one.
                var preFind = GameObjectHandler.FindGameObjects(new JObject
                {
                    ["name"] = "AfterMath_DeleteFindMe",
                    ["exactMatch"] = true
                });
                Assert.IsFalse(preFind.IsError, preFind.Error);
                Assert.AreEqual(1, (int)JObject.FromObject(preFind.Payload)["count"], "precondition: find should see the object");

                // Act
                var r = GameObjectHandler.DeleteGameObject(new JObject
                {
                    ["path"] = "/AfterMath_DeleteFindMe"
                });
                Assert.IsFalse(r.IsError, r.Error);
                Assert.AreEqual(1, (int)JObject.FromObject(r.Payload)["deletedCount"]);

                // AFTERMATH via the sibling find_gameobject read handler: now finds ZERO.
                var postFind = GameObjectHandler.FindGameObjects(new JObject
                {
                    ["name"] = "AfterMath_DeleteFindMe",
                    ["exactMatch"] = true
                });
                Assert.IsFalse(postFind.IsError, postFind.Error);
                Assert.AreEqual(0, (int)JObject.FromObject(postFind.Payload)["count"], "find must report the object gone after delete");

                target = null;
            }
            finally
            {
                if (target != null) Object.DestroyImmediate(target);
            }
        }

        // --- delete_gameobject : deleting a PARENT also removes its CHILDREN (includeChildren) -------

        [Test]
        public void DeleteGameObject_Parent_AlsoRemovesChildHierarchy()
        {
            var parent = new GameObject("AfterMath_DelParent");
            var child = new GameObject("AfterMath_DelChild");
            child.transform.SetParent(parent.transform);
            var bystander = new GameObject("AfterMath_DelBystander");
            try
            {
                Assert.IsNotNull(GameObject.Find("/AfterMath_DelParent/AfterMath_DelChild"), "precondition: child reachable by path");

                // Act
                var r = GameObjectHandler.DeleteGameObject(new JObject
                {
                    ["path"] = "/AfterMath_DelParent"
                });
                Assert.IsFalse(r.IsError, r.Error);

                // AFTERMATH: both parent and the nested child are gone; the bystander root survives.
                Assert.IsNull(GameObject.Find("/AfterMath_DelParent"), "parent should be gone");
                Assert.IsNull(GameObject.Find("/AfterMath_DelParent/AfterMath_DelChild"), "child should be gone with its parent");
                Assert.IsNotNull(GameObject.Find("/AfterMath_DelBystander"), "unrelated root must survive");

                // Destroying the parent also destroyed the child; null both so finally is a no-op.
                parent = null;
                child = null;
            }
            finally
            {
                if (parent != null) Object.DestroyImmediate(parent);
                if (child != null) Object.DestroyImmediate(child);
                if (bystander != null) Object.DestroyImmediate(bystander);
            }
        }

        // --- delete_gameobject : a NOT_FOUND delete must NOT touch existing state -------------------
        // (Complements the existing NOT_FOUND test by asserting the AFTERMATH: real state is untouched.)

        [Test]
        public void DeleteGameObject_NotFound_LeavesExistingStateUntouched()
        {
            var survivor = new GameObject("AfterMath_DelSurvivor");
            try
            {
                var r = GameObjectHandler.DeleteGameObject(new JObject
                {
                    ["path"] = "/AfterMath_NoSuchObject_Zzz"
                });

                // Outcome is a NOT_FOUND failure (per handler contract).
                Assert.IsTrue(r.IsError, "deleting a missing object should be a NOT_FOUND error");
                Assert.AreEqual("NOT_FOUND", r.Code);

                // AFTERMATH: the unrelated survivor was NOT collaterally removed.
                Assert.IsNotNull(GameObject.Find("/AfterMath_DelSurvivor"), "a failed delete must not remove unrelated objects");
            }
            finally
            {
                if (survivor != null) Object.DestroyImmediate(survivor);
            }
        }

        // --- delete_gameobject : batch with `paths` (partial — one real, one missing) ---------------

        [Test]
        public void DeleteGameObject_BatchPartial_DeletesFoundReportsMissing()
        {
            var real = new GameObject("AfterMath_BatchReal");
            try
            {
                var r = GameObjectHandler.DeleteGameObject(new JObject
                {
                    ["paths"] = new JArray { "/AfterMath_BatchReal", "/AfterMath_BatchGhost" }
                });

                // A partial delete (at least one found) stays a success per the handler contract.
                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);
                Assert.AreEqual(1, (int)payload["deletedCount"]);
                Assert.AreEqual(1, (int)payload["notFoundCount"]);

                // AFTERMATH via raw API: the real one is gone (the ghost never existed, nothing to check there).
                Assert.IsNull(GameObject.Find("/AfterMath_BatchReal"), "the found object should be deleted in a partial batch");

                real = null;
            }
            finally
            {
                if (real != null) Object.DestroyImmediate(real);
            }
        }

        // --- remove_missing_scripts : empty-effect path leaves a clean object structurally intact ----
        // The POSITIVE (actual dangling script) path is SKIPPED — see class summary: such state cannot
        // be fabricated in EditMode. Here we assert the AFTERMATH of a no-op run: the targeted clean
        // object keeps its real component count (the call did not strip a legitimate component).

        [Test]
        public void RemoveMissingScripts_CleanObject_LeavesRealComponentsIntact()
        {
            var go = new GameObject("AfterMath_MsClean", typeof(BoxCollider), typeof(Rigidbody));
            try
            {
                // Baseline captured independently of the handler.
                int componentsBefore = go.GetComponents<Component>().Length;

                var r = MissingScriptHandler.RemoveMissingScripts(new JObject
                {
                    ["gameObjectPaths"] = new JArray { "/AfterMath_MsClean" }
                });

                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);
                Assert.AreEqual(0, (int)payload["removed"], "a clean object has no missing scripts to remove");
                Assert.AreEqual(0, (int)payload["notFoundCount"], "the targeted path should resolve");

                // AFTERMATH via raw GetComponents: the real components were NOT stripped.
                int componentsAfter = go.GetComponents<Component>().Length;
                Assert.AreEqual(componentsBefore, componentsAfter,
                    "removing missing scripts on a clean object must not touch real components");
                Assert.IsNotNull(go.GetComponent<BoxCollider>(), "BoxCollider must survive");
                Assert.IsNotNull(go.GetComponent<Rigidbody>(), "Rigidbody must survive");
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
            }
        }
    }
}
