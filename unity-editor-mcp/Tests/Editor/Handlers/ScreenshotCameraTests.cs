using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    // G5: the camera capture mode renders an arbitrary world camera and returns viewable image content.
    public class ScreenshotCameraTests
    {
        [Test] public void CaptureCamera_RendersNamedCamera_ProducesImage()
        {
            var camGO = new GameObject("UemcpTestCam", typeof(Camera));
            string path = null;
            try
            {
                var outcome = ScreenshotHandler.CaptureScreenshot(new JObject
                {
                    ["captureMode"] = "camera", ["cameraName"] = "UemcpTestCam", ["width"] = 64, ["height"] = 64
                });
                Assert.IsFalse(outcome.IsError, outcome.Error);
                var data = JObject.FromObject(outcome.Payload);
                path = (string)data["path"];
                Assert.AreEqual("camera", (string)data["captureMode"]);
                Assert.AreEqual("UemcpTestCam", (string)data["camera"]["name"]);
                Assert.Greater(((string)data["image"]["data"]).Length, 0);          // viewable image content present
                Assert.AreEqual("image/png", (string)data["image"]["mimeType"]);
            }
            finally
            {
                Object.DestroyImmediate(camGO);
                if (!string.IsNullOrEmpty(path)) AssetDatabase.DeleteAsset(path);
            }
        }

        [Test] public void CaptureCamera_BogusName_NotFound()
        {
            var outcome = ScreenshotHandler.CaptureScreenshot(new JObject { ["captureMode"] = "camera", ["cameraName"] = "NoSuchCam_UemcpXyz" });
            Assert.IsTrue(outcome.IsError);
            Assert.AreEqual("CAMERA_NOT_FOUND", outcome.Code);
        }
    }
}
