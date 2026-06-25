using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath-asserting tests for the Screenshot category (ScreenshotHandler).
    ///
    /// Each test calls a handler, then INDEPENDENTLY re-reads Unity/disk state through a
    /// different path (System.IO.File / a freshly LoadImage'd Texture2D / AssetDatabase /
    /// the sibling AnalyzeScreenshot handler) and asserts the REAL effect happened AND that
    /// unrelated baseline state did not move. Every artifact (PNG on disk, the temp asset
    /// folder, and the created Camera GameObject) is restored in TearDown so zero residue
    /// is left behind.
    ///
    /// Only CAMERA mode is aftermath-tested: it renders a world Camera to a RenderTexture and
    /// works synchronously in EditMode batch (no Game View, no Scene View, no play mode).
    /// game/scene/window modes need a live editor view that is not guaranteed in batch — see
    /// the SKIP notes at the bottom of this file.
    /// </summary>
    [TestFixture]
    public class AftermathTests_Screenshot
    {
        private string tempFolderAbs;     // absolute OS path to the temp capture folder
        private string tempFolderProject; // project-relative ("Assets/...") form for AssetDatabase
        private GameObject cameraGo;

        [SetUp]
        public void Setup()
        {
            // A unique project-relative folder under Assets/ so the PathSafety.IsWithinProject guard
            // passes and AssetDatabase.Refresh can see the written PNG.
            tempFolderProject = "Assets/AftermathScreenshotTemp_" + System.Guid.NewGuid().ToString("N");
            tempFolderAbs = Path.Combine(Directory.GetCurrentDirectory(), tempFolderProject);
            Directory.CreateDirectory(tempFolderAbs);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            if (cameraGo != null)
            {
                Object.DestroyImmediate(cameraGo);
                cameraGo = null;
            }

            // Remove the temp folder (and any captured PNGs / .meta) via the asset DB first,
            // then a raw filesystem sweep as a belt-and-suspenders cleanup.
            if (!string.IsNullOrEmpty(tempFolderProject) && AssetDatabase.IsValidFolder(tempFolderProject))
            {
                AssetDatabase.DeleteAsset(tempFolderProject);
            }
            if (!string.IsNullOrEmpty(tempFolderAbs) && Directory.Exists(tempFolderAbs))
            {
                Directory.Delete(tempFolderAbs, true);
                string meta = tempFolderAbs.TrimEnd('/', '\\') + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
            }
            AssetDatabase.Refresh();
        }

        // Builds a real, render-capable camera in the active scene so CaptureCamera can resolve + render it.
        private Camera MakeCamera(string name)
        {
            cameraGo = new GameObject(name);
            var cam = cameraGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.2f, 0.4f, 0.6f, 1f);
            cam.transform.position = new Vector3(0f, 1f, -10f);
            return cam;
        }

        [Test]
        public void CaptureScreenshot_CameraMode_WritesRealPngFileToDisk()
        {
            // Arrange: a resolvable camera + a target path inside the temp folder.
            var cam = MakeCamera("AftermathCam_Write");
            string outProject = tempFolderProject + "/cam_write.png";
            string outAbs = Path.Combine(tempFolderAbs, "cam_write.png");

            // Baseline: file does not exist yet (aftermath must prove the handler created it).
            Assert.IsFalse(File.Exists(outAbs), "PNG should not exist before capture");

            var p = new JObject
            {
                ["captureMode"] = "camera",
                ["outputPath"] = outProject,
                ["cameraName"] = "AftermathCam_Write",
                ["width"] = 64,
                ["height"] = 48,
                ["encodeAsBase64"] = false
            };

            // Act
            var r = ScreenshotHandler.CaptureScreenshot(p);

            // Assert success
            Assert.IsFalse(r.IsError, r.Error);

            var payload = JObject.FromObject(r.Payload);
            Assert.AreEqual("camera", (string)payload["captureMode"]);
            Assert.AreEqual(64, (int)payload["width"]);
            Assert.AreEqual(48, (int)payload["height"]);

            // AFTERMATH via a DIFFERENT path: raw System.IO confirms a non-empty file really landed.
            Assert.IsTrue(File.Exists(outAbs), "Expected a real PNG on disk after capture");
            long len = new FileInfo(outAbs).Length;
            Assert.Greater(len, 0L, "Captured PNG must be non-empty");
            // The handler-reported fileSize must agree with the bytes actually on disk.
            Assert.AreEqual(len, (long)payload["fileSize"], "fileSize must match real file length");

            // Independently decode the bytes as a PNG and confirm the pixel dimensions match.
            byte[] bytes = File.ReadAllBytes(outAbs);
            var tex = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(tex.LoadImage(bytes), "Bytes on disk must decode as a valid image");
                Assert.AreEqual(64, tex.width, "Decoded PNG width must match requested capture width");
                Assert.AreEqual(48, tex.height, "Decoded PNG height must match requested capture height");
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }

            // Baseline unrelated state did NOT move: the camera GameObject is untouched and still in scene.
            Assert.IsNotNull(cameraGo.GetComponent<Camera>(), "Camera component must survive the capture");
            Assert.IsTrue(cam.gameObject.scene.IsValid(), "Camera must remain a live scene object");
        }

        [Test]
        public void AnalyzeScreenshot_ReadsBackDimensionsOfARealCapturedImage()
        {
            // Arrange: produce a real PNG via the camera path, then feed it to the sibling analyzer.
            MakeCamera("AftermathCam_Analyze");
            string outProject = tempFolderProject + "/cam_analyze.png";
            string outAbs = Path.Combine(tempFolderAbs, "cam_analyze.png");

            var cap = ScreenshotHandler.CaptureScreenshot(new JObject
            {
                ["captureMode"] = "camera",
                ["outputPath"] = outProject,
                ["cameraName"] = "AftermathCam_Analyze",
                ["width"] = 80,
                ["height"] = 60,
                ["encodeAsBase64"] = false
            });
            Assert.IsFalse(cap.IsError, cap.Error);
            Assert.IsTrue(File.Exists(outAbs), "Pre-condition: a real PNG must exist to analyze");

            // Act: analyze the just-captured image (chained on a real artifact).
            var r = ScreenshotHandler.AnalyzeScreenshot(new JObject
            {
                ["imagePath"] = outProject,
                ["analysisType"] = "basic"
            });

            // Assert success
            Assert.IsFalse(r.IsError, r.Error);
            var payload = JObject.FromObject(r.Payload);

            // AFTERMATH: the analyzer's reported dimensions/fileSize must match what System.IO sees
            // on the real file — an independent re-read of the same artifact through a different lens.
            Assert.AreEqual(80, (int)payload["width"], "Analyzer width must match captured width");
            Assert.AreEqual(60, (int)payload["height"], "Analyzer height must match captured height");
            long realLen = new FileInfo(outAbs).Length;
            Assert.AreEqual(realLen, (long)payload["fileSize"], "Analyzer fileSize must match real file length");
            // basic analysis attaches dominantColors + a uiElements block.
            Assert.IsNotNull(payload["dominantColors"], "basic analysis must include dominantColors");
            Assert.IsNotNull(payload["uiElements"], "basic analysis must include uiElements");

            // Baseline: analysis is read-only — the file on disk is unchanged in size after analyze.
            Assert.AreEqual(realLen, new FileInfo(outAbs).Length, "AnalyzeScreenshot must not mutate the image file");
        }

        [Test]
        public void CaptureScreenshot_OutOfProjectPath_RejectedAndWritesNothing()
        {
            // Arrange: a path that escapes the project root. The handler must refuse to write it.
            MakeCamera("AftermathCam_Escape");
            // tempFolderProject is "Assets/<temp>" — only 2 levels under the project root, so "../../" lands AT the
            // project root (still WITHIN the project, correctly allowed). Use one more "../" to escape ABOVE the
            // project root so the IsWithinProject guard must reject it.
            string escapeProject = tempFolderProject + "/../../../escape_screenshot.png";
            string escapeAbs = Path.GetFullPath(Path.Combine(tempFolderAbs, "..", "..", "..", "escape_screenshot.png"));
            bool preExisted = File.Exists(escapeAbs);

            var r = ScreenshotHandler.CaptureScreenshot(new JObject
            {
                ["captureMode"] = "camera",
                ["outputPath"] = escapeProject,
                ["cameraName"] = "AftermathCam_Escape",
                ["width"] = 32,
                ["height"] = 32,
                ["encodeAsBase64"] = false
            });

            // Assert: the guard fires with a validation error.
            Assert.IsTrue(r.IsError, "Out-of-project outputPath must be rejected");
            Assert.AreEqual("VALIDATION_ERROR", r.Code);

            // AFTERMATH via raw filesystem: no escape file was created by this call.
            if (!preExisted)
            {
                Assert.IsFalse(File.Exists(escapeAbs), "Rejected capture must not write any file outside the project");
            }
        }
    }
}
