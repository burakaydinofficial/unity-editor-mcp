using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// AFTERMATH tests for the ScriptingRead category (ScriptHandler). Each test calls a read-only /
    /// in-memory ScriptHandler tool, then INDEPENDENTLY re-reads real state (the raw file via
    /// System.IO, or the AssetDatabase) and asserts the returned data matches a KNOWN fixture, while a
    /// captured baseline does NOT move. All residue is removed.
    ///
    /// CRITICAL: nothing here creates/modifies/deletes a .cs script under Assets/ — that would force an
    /// AssetDatabase recompile + domain reload and tear down the running test. The read_script /
    /// list_scripts fixture is a .TXT file (read_script reads any path by content; it does not require
    /// the .cs extension). validate_script is fed a SOURCE STRING in memory, never a file.
    ///
    /// Covered handlers:
    ///   - validate_script (ScriptHandler.ValidateScript) — in-memory source string; valid C# reports
    ///     isValid=true with zero syntax errors, broken C# (missing closing brace) reports isValid=false
    ///     with a diagnostic. Re-read of the structured errors array proves the real diagnosis.
    ///   - read_script  (ScriptHandler.ReadScript) — a .txt fixture is written with known bytes; the
    ///     handler's returned scriptContent / lineCount / fileSize are cross-checked against the raw
    ///     file re-read via System.IO (a DIFFERENT path than the handler's AssetDatabase-free read).
    ///   - list_scripts (ScriptHandler.ListScripts) — NEGATIVE outcome: scanning a fixture folder that
    ///     holds ONLY a .txt confirms the AssetDatabase "t:Script" scan correctly returns zero scripts
    ///     (the .txt is not a script), re-read via the returned totalCount/scripts array. A POSITIVE
    ///     list_scripts (an actual .cs present) is NOT tested — see the SKIP note at the bottom.
    /// </summary>
    [TestFixture]
    public class AftermathTests_ScriptingRead
    {
        // A unique temp folder under Assets/ for the read_script / list_scripts file fixtures. Removed
        // in TearDown. Only .txt (and a .meta) ever live here, so no recompile is ever triggered.
        private const string FixtureFolderRel = "Assets/__AftermathScriptingReadTmp__";
        private string _fixtureFolderAbs;

        [SetUp]
        public void Setup()
        {
            _fixtureFolderAbs = Path.Combine(Application.dataPath, "../", FixtureFolderRel);
            // Defensive pre-clean of any residue from a previously interrupted run.
            if (AssetDatabase.IsValidFolder(FixtureFolderRel))
            {
                AssetDatabase.DeleteAsset(FixtureFolderRel);
            }
            if (Directory.Exists(_fixtureFolderAbs))
            {
                Directory.Delete(_fixtureFolderAbs, true);
            }
            Directory.CreateDirectory(_fixtureFolderAbs);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            // Remove the whole fixture folder (and its .meta) — zero residue, no .cs ever existed here.
            if (AssetDatabase.IsValidFolder(FixtureFolderRel))
            {
                AssetDatabase.DeleteAsset(FixtureFolderRel);
            }
            if (Directory.Exists(_fixtureFolderAbs))
            {
                Directory.Delete(_fixtureFolderAbs, true);
            }
            AssetDatabase.Refresh();
        }

        // ------------------------------------------------------------------
        // validate_script — VALID source string (in memory, no file).
        // Aftermath: the handler reports isValid=true and an EMPTY errors array. We independently
        // confirm the structured result (not just "no handler error"): a syntactically balanced C#
        // class must produce zero syntax-error entries.
        // ------------------------------------------------------------------
        [Test]
        public void ValidateScript_WellFormedSource_ReportsValidWithNoErrors()
        {
            // KNOWN fixture: a perfectly balanced, trivially-correct C# class as a STRING.
            string validSource =
                "using UnityEngine;\n" +
                "public class WellFormedProbe : MonoBehaviour\n" +
                "{\n" +
                "    void Start()\n" +
                "    {\n" +
                "        Debug.Log(\"ok\");\n" +
                "    }\n" +
                "}\n";

            var r = ScriptHandler.ValidateScript(new JObject
            {
                ["scriptContent"] = validSource,
                ["checkSyntax"] = true,
                ["checkUnityCompatibility"] = true
            });

            Assert.IsFalse(r.IsError, r.Error);
            var payload = JObject.FromObject(r.Payload);

            // AFTERMATH: the structured validation result says VALID with zero syntax errors.
            Assert.IsTrue((bool)payload["isValid"], "balanced, correct C# must validate as isValid=true");
            var errors = (JArray)payload["errors"];
            Assert.IsNotNull(errors, "the result must carry a structured errors array");
            Assert.AreEqual(0, errors.Count, "well-formed source must produce zero syntax errors");
        }

        // ------------------------------------------------------------------
        // validate_script — BROKEN source string (missing closing brace).
        // Aftermath: the handler reports isValid=false and surfaces a concrete syntax diagnostic about
        // the missing brace. We re-read the structured errors array and assert the count + message —
        // proving it actually DIAGNOSED the defect, not merely "did not throw".
        // ------------------------------------------------------------------
        [Test]
        public void ValidateScript_MissingClosingBrace_ReportsInvalidWithDiagnostic()
        {
            // KNOWN defect: the class body's closing brace is missing -> unbalanced braces (one open,
            // never closed). ValidateBasicSyntax must flag "Missing closing brace(s)".
            string brokenSource =
                "using UnityEngine;\n" +
                "public class BrokenProbe : MonoBehaviour\n" +
                "{\n" +
                "    void Start()\n" +
                "    {\n" +
                "        Debug.Log(\"oops\");\n" +
                "    }\n";   // <- the class-level closing '}' is intentionally absent

            var r = ScriptHandler.ValidateScript(new JObject
            {
                ["scriptContent"] = brokenSource,
                ["checkSyntax"] = true,
                ["checkUnityCompatibility"] = false
            });

            // The handler itself succeeds (validation is its job); the VERDICT is "invalid".
            Assert.IsFalse(r.IsError, r.Error);
            var payload = JObject.FromObject(r.Payload);

            // AFTERMATH: the structured verdict is INVALID and a real brace diagnostic is present.
            Assert.IsFalse((bool)payload["isValid"], "source with an unbalanced brace must validate as isValid=false");
            var errors = (JArray)payload["errors"];
            Assert.IsNotNull(errors, "the result must carry a structured errors array");
            Assert.GreaterOrEqual(errors.Count, 1, "a missing closing brace must produce at least one syntax error");

            bool sawBraceError = errors.Any(e =>
            {
                var msg = (string)e["message"];
                return msg != null && msg.IndexOf("brace", StringComparison.OrdinalIgnoreCase) >= 0;
            });
            Assert.IsTrue(sawBraceError, "the diagnostic must concretely call out the missing brace");
        }

        // ------------------------------------------------------------------
        // read_script — round-trip a .txt fixture by path.
        // Aftermath: the handler's returned scriptContent equals the exact bytes we wrote, and its
        // reported metadata (lineCount, fileSize) matches an INDEPENDENT System.IO re-read of the same
        // file. We capture the raw file bytes as the baseline-of-truth (read_script must NOT mutate the
        // file) and re-verify them after the call.
        // ------------------------------------------------------------------
        [Test]
        public void ReadScript_TxtFixture_ReturnsExactContentAndMetadata()
        {
            // KNOWN fixture: deterministic multi-line content with a trailing newline. Using .txt (not
            // .cs) guarantees no recompile. read_script reads by path with no extension filter.
            string fixtureRel = FixtureFolderRel + "/ReadProbe.txt";
            string fixtureAbs = Path.Combine(Application.dataPath, "../", fixtureRel);
            string knownContent = "line one\nline two\nline three\n";
            File.WriteAllText(fixtureAbs, knownContent);
            AssetDatabase.Refresh();

            // Baseline-of-truth captured via an INDEPENDENT API (System.IO) BEFORE the handler runs.
            byte[] preBytes = File.ReadAllBytes(fixtureAbs);
            long expectedSize = new FileInfo(fixtureAbs).Length;
            int expectedLineCount = knownContent.Split('\n').Length; // mirrors the handler's own formula

            var r = ScriptHandler.ReadScript(new JObject
            {
                ["scriptPath"] = fixtureRel,
                ["includeMetadata"] = true
            });

            Assert.IsFalse(r.IsError, r.Error);
            var payload = JObject.FromObject(r.Payload);

            // AFTERMATH: returned content + path + metadata match the known fixture exactly.
            Assert.AreEqual(knownContent, (string)payload["scriptContent"],
                "read_script must return the exact bytes that were written to the file");
            Assert.AreEqual(fixtureRel, (string)payload["scriptPath"],
                "read_script must echo back the path it read");
            Assert.AreEqual(expectedLineCount, (int)payload["lineCount"],
                "the reported lineCount must match the known fixture's line count");
            Assert.AreEqual(expectedSize, (long)payload["fileSize"],
                "the reported fileSize must match the file's real byte length");

            // AFTERMATH (baseline did not move): read_script is read-only — the file's bytes are
            // byte-for-byte unchanged after the call.
            byte[] postBytes = File.ReadAllBytes(fixtureAbs);
            Assert.AreEqual(preBytes, postBytes, "read_script must NOT mutate the file it reads");
        }

        // ------------------------------------------------------------------
        // list_scripts — NEGATIVE outcome over a script-free folder.
        // Aftermath: the AssetDatabase "t:Script" scan of a folder that contains ONLY a .txt must
        // return zero scripts. This proves the scan really discriminates by asset type (a .txt is not a
        // script) rather than blindly listing files. Re-read via the returned totalCount + scripts
        // array; cross-checked against an INDEPENDENT AssetDatabase.FindAssets call.
        // ------------------------------------------------------------------
        [Test]
        public void ListScripts_FolderWithOnlyTxt_ReturnsNoScripts()
        {
            // KNOWN fixture: a single non-script .txt inside the temp folder. No .cs anywhere here.
            string fixtureRel = FixtureFolderRel + "/NotAScript.txt";
            string fixtureAbs = Path.Combine(Application.dataPath, "../", fixtureRel);
            File.WriteAllText(fixtureAbs, "this is plainly not a C# script\n");
            AssetDatabase.Refresh();

            // INDEPENDENT baseline: Unity's own type-scan agrees there are zero scripts in the folder.
            string[] independentScan = AssetDatabase.FindAssets("t:Script", new[] { FixtureFolderRel });
            Assert.AreEqual(0, independentScan.Length,
                "precondition: the fixture folder must contain no scripts by Unity's own type scan");

            var r = ScriptHandler.ListScripts(new JObject
            {
                ["searchPath"] = FixtureFolderRel,
                ["includeMetadata"] = false
            });

            Assert.IsFalse(r.IsError, r.Error);
            var payload = JObject.FromObject(r.Payload);

            // AFTERMATH: the handler agrees — zero scripts found in a folder holding only a .txt.
            Assert.AreEqual(0, (int)payload["totalCount"],
                "list_scripts must report zero scripts for a folder containing only a .txt file");
            var scripts = (JArray)payload["scripts"];
            Assert.IsNotNull(scripts, "the result must carry a scripts array");
            Assert.AreEqual(0, scripts.Count,
                "the scripts array must be empty — a .txt is not a script");
        }

        // ------------------------------------------------------------------
        // SKIP — list_scripts POSITIVE case (a real .cs present in the folder).
        // A faithful positive aftermath would require an actual .cs file under Assets/ so the
        // AssetDatabase "t:Script" scan would index it. Importing a new .cs forces a recompile +
        // domain reload, which tears down the running test session — forbidden. The NEGATIVE outcome
        // above (script-free folder -> zero scripts) is the strongest list_scripts assertion possible
        // without creating a script, so the positive path is intentionally not tested here.
        //
        // SKIP — create_script / update_script / delete_script: each writes/imports/removes a .cs and
        // calls AssetDatabase.Refresh(), triggering a recompile + domain reload that would tear down
        // the test run. Out of scope for EditMode aftermath testing.
        // ------------------------------------------------------------------
    }
}
