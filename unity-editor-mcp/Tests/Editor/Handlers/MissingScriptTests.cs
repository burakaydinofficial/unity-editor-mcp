using NUnit.Framework;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // F5: the scan/report/gate wrapper over Unity's missing-script APIs. (The positive path — an actual dangling
    // script — can't be fabricated cleanly in EditMode; it relies on GameObjectUtility, which Unity owns.)
    public class MissingScriptTests
    {
        [Test] public void FindMissingScripts_CleanScene_ReportsNoneWithShape()
        {
            var go = new GameObject("MsCleanFixture", typeof(BoxCollider));
            try
            {
                var data = JObject.FromObject(MissingScriptHandler.FindMissingScripts(new JObject()).Payload);
                Assert.AreEqual(0, (int)data["totalMissing"]); // a clean object contributes nothing
                Assert.IsNotNull(data["objects"]);
                Assert.IsFalse((bool)data["truncated"]);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void RemoveMissingScripts_NothingMissing_RemovesZero()
        {
            var go = new GameObject("MsCleanFixture2", typeof(BoxCollider));
            try
            {
                var r = MissingScriptHandler.RemoveMissingScripts(new JObject());
                Assert.IsFalse(r.IsError, r.Error);
                Assert.AreEqual(0, (int)JObject.FromObject(r.Payload)["removed"]);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void RemoveMissingScripts_BogusPath_ReportedNotFound()
        {
            var r = MissingScriptHandler.RemoveMissingScripts(new JObject { ["gameObjectPaths"] = new JArray { "/NoSuchObject_MsXyz" } });
            Assert.IsFalse(r.IsError, r.Error);
            var data = JObject.FromObject(r.Payload);
            Assert.AreEqual(1, (int)data["notFoundCount"]);
            Assert.AreEqual(0, (int)data["removed"]);
        }
    }
}
