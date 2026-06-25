using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Handlers;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Aftermath tests for the 3 STATIC uGUI tools (UIInteractionHandler): find_ui_elements,
    /// get_ui_element_state, set_ui_element_value. Each test builds a KNOWN legacy-uGUI fixture
    /// (UnityEngine.UI — NOT TMP) under a Canvas in the live scene, calls the tool HANDLER, then
    /// INDEPENDENTLY re-reads the RAW UnityEngine.UI component (a path the handler did not produce) and
    /// asserts the returned/changed data matches that ground truth — not merely that the call did not
    /// error. Every test destroys the whole Canvas GameObject in a finally so it leaves zero residue.
    ///
    /// These are STATIC-state tests: NO play mode, NO EventSystem, NO simulated clicks/pointer input —
    /// that surface (click_ui_element / simulate_ui_input) is runtime and is intentionally NOT tested here.
    ///
    /// Addressing: every handler resolves an element via GameObject.Find(elementPath), which only finds
    /// ACTIVE objects and matches the scene hierarchy path. The fixtures are built active at scene root, so
    /// the addressable path is "/<CanvasName>/<ElementName>" (matches the handler's own GetGameObjectPath).
    ///
    /// Legacy uGUI types (Canvas/Button/Slider/Toggle/InputField/Text from UnityEngine.UI) exist since
    /// 2019.4 — floor-safe, no #if guard, no new guarded API introduced by this file.
    /// C# 7.3 / netstandard 2.0 (2019.4 floor): no C# 8+ syntax.
    /// </summary>
    public class AftermathTests_UI
    {
        private const string CanvasName = "AftermathUICanvas";

        // ---------------------------------------------------------------------------------------------
        // find_ui_elements
        // ---------------------------------------------------------------------------------------------
        // OUTCOME asserted: the returned element set CONTAINS our known fixtures, matched by the exact
        // {name, elementType, path} the handler reports — cross-checked against what we created (a Button
        // named UI_FixtureButton and a Slider named UI_FixtureSlider). elementType is the component type
        // name, so a Button GameObject contributes a "Button" entry (and an Image entry); the Slider
        // contributes a "Slider" entry.

        [Test]
        public void FindUIElements_ReturnsKnownNamedElements_MatchingRawFixture()
        {
            GameObject canvasGo = null;
            try
            {
                // Arrange a KNOWN fixture under an active Canvas at scene root.
                canvasGo = new GameObject(CanvasName, typeof(Canvas));

                var buttonGo = new GameObject("UI_FixtureButton", typeof(Image), typeof(Button));
                buttonGo.transform.SetParent(canvasGo.transform, false);

                var sliderGo = new GameObject("UI_FixtureSlider", typeof(Slider));
                sliderGo.transform.SetParent(canvasGo.transform, false);

                // Independent ground truth straight off the raw components.
                var rawButton = buttonGo.GetComponent<Button>();
                var rawSlider = sliderGo.GetComponent<Slider>();
                Assert.IsNotNull(rawButton, "precondition: fixture Button component should exist");
                Assert.IsNotNull(rawSlider, "precondition: fixture Slider component should exist");
                string expectedButtonPath = "/" + CanvasName + "/UI_FixtureButton";
                string expectedSliderPath = "/" + CanvasName + "/UI_FixtureSlider";

                // Act — scope the scan to our canvas so other scene canvases can't perturb the assertions.
                var r = UIInteractionHandler.FindUIElements(new JObject
                {
                    ["canvasFilter"] = CanvasName
                });

                // Assert outcome
                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);
                var elements = (JArray)payload["elements"];
                Assert.IsNotNull(elements, "elements array should be present");
                Assert.AreEqual(elements.Count, (int)payload["count"], "count must equal the elements array length");

                // AFTERMATH — the known Button is in the set, matched by name + elementType + path.
                var buttonEntry = elements.OfType<JObject>().FirstOrDefault(e =>
                    (string)e["name"] == "UI_FixtureButton" && (string)e["elementType"] == "Button");
                Assert.IsNotNull(buttonEntry, "find_ui_elements must report the Button by name+type");
                Assert.AreEqual(expectedButtonPath, (string)buttonEntry["path"],
                    "reported Button path must equal the raw hierarchy path");
                Assert.AreEqual(rawButton.interactable, (bool)buttonEntry["isInteractable"],
                    "reported Button isInteractable must equal the raw Button.interactable");
                Assert.IsTrue((bool)buttonEntry["isActive"], "fixture Button is active in hierarchy");

                // AFTERMATH — the known Slider is in the set, matched by name + elementType + path.
                var sliderEntry = elements.OfType<JObject>().FirstOrDefault(e =>
                    (string)e["name"] == "UI_FixtureSlider" && (string)e["elementType"] == "Slider");
                Assert.IsNotNull(sliderEntry, "find_ui_elements must report the Slider by name+type");
                Assert.AreEqual(expectedSliderPath, (string)sliderEntry["path"],
                    "reported Slider path must equal the raw hierarchy path");
                Assert.AreEqual(rawSlider.interactable, (bool)sliderEntry["isInteractable"],
                    "reported Slider isInteractable must equal the raw Slider.interactable");

                // Cross-check: every reported element's canvasPath points back at our fixture canvas.
                string expectedCanvasPath = "/" + CanvasName;
                foreach (var e in elements.OfType<JObject>())
                {
                    Assert.AreEqual(expectedCanvasPath, (string)e["canvasPath"],
                        "every element under the filtered canvas must report that canvas as its canvasPath");
                }
            }
            finally
            {
                if (canvasGo != null) Object.DestroyImmediate(canvasGo);
            }

            // Zero residue.
            Assert.IsNull(GameObject.Find("/" + CanvasName), "fixture Canvas must be destroyed after the test");
        }

        // ---------------------------------------------------------------------------------------------
        // get_ui_element_state
        // ---------------------------------------------------------------------------------------------
        // OUTCOME asserted: the reported state fields equal the RAW component state we set on a Toggle with
        // a KNOWN static state — isOn = true AND interactable = false. The handler reads isOn off the
        // Toggle and isInteractable off the Selectable; both must round-trip from the live component.

        [Test]
        public void GetUIElementState_ReportsRawToggleState_IsOnAndInteractable()
        {
            GameObject canvasGo = null;
            try
            {
                // Arrange a KNOWN Toggle state: ON and NON-interactable (a distinct, non-default combo).
                canvasGo = new GameObject(CanvasName, typeof(Canvas));

                var toggleGo = new GameObject("UI_FixtureToggle", typeof(Toggle));
                toggleGo.transform.SetParent(canvasGo.transform, false);

                var rawToggle = toggleGo.GetComponent<Toggle>();
                rawToggle.isOn = true;
                rawToggle.interactable = false;

                string togglePath = "/" + CanvasName + "/UI_FixtureToggle";
                Assert.IsNotNull(GameObject.Find(togglePath), "precondition: toggle resolvable by path");

                // Act
                var r = UIInteractionHandler.GetUIElementState(new JObject
                {
                    ["elementPath"] = togglePath,
                    ["includeInteractableInfo"] = true
                });

                // Assert outcome
                Assert.IsFalse(r.IsError, r.Error);
                var state = JObject.FromObject(r.Payload);

                // AFTERMATH — reported fields equal the RAW component (re-read independently below).
                Assert.AreEqual(togglePath, (string)state["path"], "reported path must equal the resolved path");
                Assert.AreEqual("UI_FixtureToggle", (string)state["name"], "reported name mismatch");
                Assert.AreEqual(rawToggle.isOn, (bool)state["isOn"], "reported isOn must equal the raw Toggle.isOn");
                Assert.AreEqual(rawToggle.interactable, (bool)state["isInteractable"],
                    "reported isInteractable must equal the raw Toggle.interactable");

                // Belt-and-braces: the live component still holds exactly the state we set (read did not mutate).
                Assert.IsTrue(rawToggle.isOn, "raw Toggle.isOn must remain true after a read");
                Assert.IsFalse(rawToggle.interactable, "raw Toggle.interactable must remain false after a read");
            }
            finally
            {
                if (canvasGo != null) Object.DestroyImmediate(canvasGo);
            }

            Assert.IsNull(GameObject.Find("/" + CanvasName), "fixture Canvas must be destroyed after the test");
        }

        // ---------------------------------------------------------------------------------------------
        // set_ui_element_value
        // ---------------------------------------------------------------------------------------------
        // OUTCOME asserted: after the handler sets the value, the RAW legacy InputField.text actually
        // changed to exactly what we passed (independent re-read of the live component), having started
        // from a different known value. The success payload's newValue is also cross-checked.

        [Test]
        public void SetUIElementValue_MutatesRawInputFieldText_ToSuppliedValue()
        {
            GameObject canvasGo = null;
            try
            {
                // Arrange a legacy uGUI InputField with a KNOWN starting text, wired with its required
                // Text component (textComponent) the way uGUI expects.
                canvasGo = new GameObject(CanvasName, typeof(Canvas));

                var inputGo = new GameObject("UI_FixtureInput", typeof(Image), typeof(InputField));
                inputGo.transform.SetParent(canvasGo.transform, false);

                var textGo = new GameObject("Text", typeof(Text));
                textGo.transform.SetParent(inputGo.transform, false);

                var rawInput = inputGo.GetComponent<InputField>();
                rawInput.textComponent = textGo.GetComponent<Text>();
                rawInput.text = "BEFORE_VALUE";

                string inputPath = "/" + CanvasName + "/UI_FixtureInput";
                Assert.AreEqual("BEFORE_VALUE", rawInput.text, "precondition: starting text is the known baseline");

                const string newText = "AFTER_VALUE_42";

                // Act — disable event triggering: this is a static-state mutation, not an input simulation.
                var r = UIInteractionHandler.SetUIElementValue(new JObject
                {
                    ["elementPath"] = inputPath,
                    ["value"] = newText,
                    ["triggerEvents"] = false
                });

                // Assert outcome
                Assert.IsFalse(r.IsError, r.Error);
                var payload = JObject.FromObject(r.Payload);
                Assert.IsTrue((bool)payload["success"], "handler should report success");
                Assert.AreEqual(newText, (string)payload["newValue"], "reported newValue must echo what we set");

                // AFTERMATH — INDEPENDENT re-read of the live component: the raw .text actually changed.
                Assert.AreEqual(newText, rawInput.text,
                    "raw InputField.text must have changed to the supplied value");
                Assert.AreNotEqual("BEFORE_VALUE", rawInput.text, "the old value must be gone");
            }
            finally
            {
                if (canvasGo != null) Object.DestroyImmediate(canvasGo);
            }

            Assert.IsNull(GameObject.Find("/" + CanvasName), "fixture Canvas must be destroyed after the test");
        }
    }
}
