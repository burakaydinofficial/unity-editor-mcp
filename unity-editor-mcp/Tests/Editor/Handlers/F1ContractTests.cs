using NUnit.Framework;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.TestTools;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // F1 contract-sweep fixes: an op that did NOT happen must not report success.
    public class F1ContractTests
    {
        [Test] public void ExecuteMenuItem_BogusPath_FailsNotFalseSuccess()
        {
            // Unity itself logs an error for an unknown menu path; expect it so it isn't treated as a test failure.
            LogAssert.Expect(LogType.Error, new Regex("ExecuteMenuItem failed because there is no menu"));
            var r = MenuHandler.ExecuteMenuItem(new JObject { ["menuPath"] = "NoSuch/Bogus/Menu_F1Xyz" });
            Assert.IsTrue(r.IsError, "a menu that did not execute must NOT report success:true"); // was success:true, executed:false
        }
    }
}
