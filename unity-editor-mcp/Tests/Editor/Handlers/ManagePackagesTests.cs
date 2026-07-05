using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// manage_packages VALIDATION paths only. A valid add/update/remove calls UnityEditor.PackageManager.Client,
    /// which resolves asynchronously and triggers a domain reload — untestable in EditMode — so these pin the
    /// input guards that reject BEFORE any Client call (packageId sanity + the action enum incl. the new "update").
    /// The confirm gate itself (H3) is enforced centrally by the CommandDispatcher, covered in the Core dotnet tests.
    /// </summary>
    public class ManagePackagesTests
    {
        [Test]
        public void ManagePackages_MissingPackageId_IsValidationError()
        {
            var outcome = EditorInfoHandler.ManagePackages(new JObject { ["action"] = "add" });
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
            StringAssert.Contains("packageId", outcome.Error);
        }

        [Test]
        public void ManagePackages_ControlCharInPackageId_IsRejected()
        {
            // A newline could smuggle a second value; reject before it reaches Client.Add.
            var outcome = EditorInfoHandler.ManagePackages(new JObject { ["action"] = "add", ["packageId"] = "com.unity.textmeshpro\nrm -rf" });
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
            StringAssert.Contains("control character", outcome.Error);
        }

        [Test]
        public void ManagePackages_OverLongPackageId_IsRejected()
        {
            var outcome = EditorInfoHandler.ManagePackages(new JObject { ["action"] = "add", ["packageId"] = new string('a', 513) });
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
            StringAssert.Contains("too long", outcome.Error);
        }

        [Test]
        public void ManagePackages_UnknownAction_ListsAddUpdateRemove()
        {
            var outcome = EditorInfoHandler.ManagePackages(new JObject { ["action"] = "install", ["packageId"] = "com.unity.textmeshpro" });
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual("VALIDATION_ERROR", outcome.Code);
            StringAssert.Contains("add, update, remove", outcome.Error);
        }
    }
}
