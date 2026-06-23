using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEditor;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Handlers
{
    /// <summary>
    /// Handles UI interaction operations
    /// </summary>
    public static class UIInteractionHandler
    {
        /// <summary>
        /// Finds UI elements based on various filters
        /// </summary>
        public static HandlerOutcome FindUIElements(JObject parameters)
        {
            try
            {
                // Parse parameters
                string elementType = parameters["elementType"]?.ToString();
                string tagFilter = parameters["tagFilter"]?.ToString();
                string namePattern = parameters["namePattern"]?.ToString();
                bool includeInactive = parameters["includeInactive"]?.ToObject<bool>() ?? false;
                string canvasFilter = parameters["canvasFilter"]?.ToString();

                // Find all canvases in the scene
                Canvas[] allCanvases = includeInactive
                    ? Resources.FindObjectsOfTypeAll<Canvas>()
                    : UnityEngine.Object.FindObjectsOfType<Canvas>();

                List<object> elements = new List<object>();

                // Search through each canvas
                foreach (var canvas in allCanvases)
                {
                    // Skip if canvas filter doesn't match
                    if (!string.IsNullOrEmpty(canvasFilter) && canvas.name != canvasFilter)
                        continue;

                    // Get all UI components in the canvas
                    var allComponents = includeInactive
                        ? canvas.GetComponentsInChildren<Component>(true)
                        : canvas.GetComponentsInChildren<Component>(false);

                    foreach (var component in allComponents)
                    {
                        // Skip if not a UI component
                        if (!IsUIComponent(component))
                            continue;

                        // Apply filters
                        if (!PassesFilters(component, elementType, tagFilter, namePattern))
                            continue;

                        // Get interactable status
                        bool isInteractable = GetInteractableStatus(component);

                        // Create element info
                        var elementInfo = new
                        {
                            path = GetGameObjectPath(component.gameObject),
                            elementType = component.GetType().Name,
                            name = component.gameObject.name,
                            isActive = component.gameObject.activeInHierarchy,
                            isInteractable = isInteractable,
                            tag = component.gameObject.tag,
                            canvasPath = GetGameObjectPath(canvas.gameObject)
                        };

                        elements.Add(elementInfo);
                    }
                }

                return HandlerOutcome.Ok(new
                {
                    elements = elements,
                    count = elements.Count
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[UIInteractionHandler] Error in FindUIElements: {e.Message}");
                return HandlerOutcome.Fail($"Failed to find UI elements: {e.Message}");
            }
        }

        /// <summary>
        /// Clicks on a UI element
        /// </summary>
        public static HandlerOutcome ClickUIElement(JObject parameters)
        {
            try
            {
                string elementPath = parameters["elementPath"]?.ToString();
                string clickType = parameters["clickType"]?.ToString() ?? "left";
                float holdDuration = parameters["holdDuration"]?.ToObject<float>() ?? 0f;

                if (string.IsNullOrEmpty(elementPath))
                {
                    return HandlerOutcome.Fail("elementPath is required", "VALIDATION_ERROR");
                }

                // Find the GameObject
                GameObject targetObject = GameObject.Find(elementPath);
                if (targetObject == null)
                {
                    return HandlerOutcome.Fail($"UI element not found at path: {elementPath}", "NOT_FOUND");
                }

                // Check if it's a UI element
                var uiComponent = targetObject.GetComponent<Graphic>();
                if (uiComponent == null)
                {
                    return HandlerOutcome.Fail($"GameObject at {elementPath} is not a UI element", "INVALID_STATE");
                }

                // Check if interactable
                var selectable = targetObject.GetComponent<Selectable>();
                if (selectable != null && !selectable.interactable)
                {
                    return HandlerOutcome.Fail($"UI element at {elementPath} is not interactable", "INVALID_STATE");
                }

                // Simulate click based on component type
                bool success = SimulateClick(targetObject, clickType, holdDuration);

                if (!success)
                {
                    return HandlerOutcome.Fail("Failed to simulate click on UI element");
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    elementPath = elementPath,
                    clickType = clickType,
                    message = $"Successfully clicked {targetObject.name}"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[UIInteractionHandler] Error in ClickUIElement: {e.Message}");
                return HandlerOutcome.Fail($"Failed to click UI element: {e.Message}");
            }
        }

        /// <summary>
        /// Gets the state of a UI element
        /// </summary>
        public static HandlerOutcome GetUIElementState(JObject parameters)
        {
            try
            {
                string elementPath = parameters["elementPath"]?.ToString();
                bool includeChildren = parameters["includeChildren"]?.ToObject<bool>() ?? false;
                bool includeInteractableInfo = parameters["includeInteractableInfo"]?.ToObject<bool>() ?? true;

                if (string.IsNullOrEmpty(elementPath))
                {
                    return HandlerOutcome.Fail("elementPath is required", "VALIDATION_ERROR");
                }

                GameObject targetObject = GameObject.Find(elementPath);
                if (targetObject == null)
                {
                    return HandlerOutcome.Fail($"UI element not found at path: {elementPath}", "NOT_FOUND");
                }

                var state = GetElementState(targetObject, includeChildren, includeInteractableInfo);
                return HandlerOutcome.Ok(state);
            }
            catch (Exception e)
            {
                Debug.LogError($"[UIInteractionHandler] Error in GetUIElementState: {e.Message}");
                return HandlerOutcome.Fail($"Failed to get UI element state: {e.Message}");
            }
        }

        /// <summary>
        /// Sets the value of a UI element
        /// </summary>
        public static HandlerOutcome SetUIElementValue(JObject parameters)
        {
            try
            {
                string elementPath = parameters["elementPath"]?.ToString();
                var value = parameters["value"];
                bool triggerEvents = parameters["triggerEvents"]?.ToObject<bool>() ?? true;

                if (string.IsNullOrEmpty(elementPath))
                {
                    return HandlerOutcome.Fail("elementPath is required", "VALIDATION_ERROR");
                }

                if (value == null)
                {
                    return HandlerOutcome.Fail("value is required", "VALIDATION_ERROR");
                }

                GameObject targetObject = GameObject.Find(elementPath);
                if (targetObject == null)
                {
                    return HandlerOutcome.Fail($"UI element not found at path: {elementPath}", "NOT_FOUND");
                }

                bool success = SetElementValue(targetObject, value, triggerEvents);
                if (!success)
                {
                    return HandlerOutcome.Fail("Failed to set UI element value - unsupported element type", "INVALID_STATE");
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    elementPath = elementPath,
                    newValue = value.ToString(),
                    message = $"Successfully set value for {targetObject.name}"
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[UIInteractionHandler] Error in SetUIElementValue: {e.Message}");
                return HandlerOutcome.Fail($"Failed to set UI element value: {e.Message}");
            }
        }

        /// <summary>
        /// Simulates a complex UI input sequence
        /// </summary>
        public static HandlerOutcome SimulateUIInput(JObject parameters)
        {
            try
            {
                var inputSequence = parameters["inputSequence"]?.ToObject<JArray>();
                float waitBetween = parameters["waitBetween"]?.ToObject<float>() ?? 100f;
                bool validateState = parameters["validateState"]?.ToObject<bool>() ?? true;

                // Simple mode: a single {elementPath, inputType, inputData} translates to a one-action sequence.
                if ((inputSequence == null || inputSequence.Count == 0) && !string.IsNullOrEmpty(parameters["elementPath"]?.ToString()))
                {
                    string simplePath = parameters["elementPath"].ToString();
                    string simpleType = (parameters["inputType"]?.ToString() ?? "click").ToLowerInvariant();
                    string simpleData = parameters["inputData"]?.ToString();
                    JObject single;
                    if (simpleType == "click")
                        single = new JObject { ["type"] = "click", ["params"] = new JObject { ["elementPath"] = simplePath } };
                    else if (simpleType == "type" || simpleType == "setvalue")
                        single = new JObject { ["type"] = "setvalue", ["params"] = new JObject { ["elementPath"] = simplePath, ["value"] = simpleData } };
                    else
                        return HandlerOutcome.Fail($"Simple inputType '{simpleType}' is not supported — use 'click' or 'type', or pass an inputSequence.", "VALIDATION_ERROR");
                    inputSequence = new JArray { single };
                }

                if (inputSequence == null || inputSequence.Count == 0)
                {
                    return HandlerOutcome.Fail("Provide either an inputSequence, or elementPath + inputType (simple mode).", "VALIDATION_ERROR");
                }

                List<object> results = new List<object>();

                foreach (JObject action in inputSequence)
                {
                    string actionType = action["type"]?.ToString();
                    var actionParams = action["params"] as JObject;

                    if (string.IsNullOrEmpty(actionType) || actionParams == null)
                    {
                        results.Add(new { error = "Invalid action format" });
                        continue;
                    }

                    // Execute action based on type
                    object result = null;
                    switch (actionType.ToLower())
                    {
                        case "click":
                            result = Flatten(ClickUIElement(actionParams));
                            break;
                        case "setvalue":
                            result = Flatten(SetUIElementValue(actionParams));
                            break;
                        default:
                            result = new { error = $"Unknown action type: {actionType}" };
                            break;
                    }

                    results.Add(result);

                    // Wait between actions if needed
                    if (waitBetween > 0 && inputSequence.IndexOf(action) < inputSequence.Count - 1)
                    {
                        // Note: In real implementation, this would need to be async
                        // For now, we're just recording the wait time
                        results.Add(new { wait = waitBetween });
                    }
                }

                return HandlerOutcome.Ok(new
                {
                    success = true,
                    results = results,
                    totalActions = inputSequence.Count
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[UIInteractionHandler] Error in SimulateUIInput: {e.Message}");
                return HandlerOutcome.Fail($"Failed to simulate UI input: {e.Message}");
            }
        }

        // SimulateUIInput aggregates per-action results into one payload, so the inner click/setvalue
        // calls must contribute their flat result shape — not a nested HandlerOutcome. Ok → the original
        // payload object; Fail → { error } (matching the pre-migration per-action error shape).
        private static object Flatten(HandlerOutcome outcome) =>
            outcome.IsError ? (object)new { error = outcome.Error } : outcome.Payload;

        #region Helper Methods

        private static bool IsUIComponent(Component component)
        {
            // Check if component is a UI component
            return component is Graphic || 
                   component is Selectable || 
                   component is LayoutGroup ||
                   component is ContentSizeFitter ||
                   component is AspectRatioFitter ||
                   component is CanvasScaler ||
                   component is GraphicRaycaster;
        }

        private static bool PassesFilters(Component component, string elementType, string tagFilter, string namePattern)
        {
            // Check element type filter
            if (!string.IsNullOrEmpty(elementType))
            {
                string componentType = component.GetType().Name;
                if (!componentType.Equals(elementType, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Check tag filter
            if (!string.IsNullOrEmpty(tagFilter))
            {
                if (component.gameObject.tag != tagFilter)
                    return false;
            }

            // Check name pattern
            if (!string.IsNullOrEmpty(namePattern))
            {
                try
                {
                    Regex regex = new Regex(namePattern);
                    if (!regex.IsMatch(component.gameObject.name))
                        return false;
                }
                catch
                {
                    // If regex is invalid, do simple contains check
                    if (!component.gameObject.name.Contains(namePattern))
                        return false;
                }
            }

            return true;
        }

        private static bool GetInteractableStatus(Component component)
        {
            var selectable = component as Selectable;
            if (selectable != null)
                return selectable.interactable;

            var button = component as Button;
            if (button != null)
                return button.interactable;

            var toggle = component as Toggle;
            if (toggle != null)
                return toggle.interactable;

            var inputField = component as InputField;
            if (inputField != null)
                return inputField.interactable;

            var slider = component as Slider;
            if (slider != null)
                return slider.interactable;

            var dropdown = component as Dropdown;
            if (dropdown != null)
                return dropdown.interactable;

            // For non-selectable UI elements, consider them interactable if active
            return component.gameObject.activeInHierarchy;
        }

        private static string GetGameObjectPath(GameObject obj)
        {
            string path = "/" + obj.name;
            Transform parent = obj.transform.parent;
            
            while (parent != null)
            {
                path = "/" + parent.name + path;
                parent = parent.parent;
            }
            
            return path;
        }

        private static bool SimulateClick(GameObject target, string clickType, float holdDuration)
        {
            // Find button component
            var button = target.GetComponent<Button>();
            if (button != null && button.interactable)
            {
                button.onClick.Invoke();
                return true;
            }

            // Find toggle component
            var toggle = target.GetComponent<Toggle>();
            if (toggle != null && toggle.interactable)
            {
                Undo.RecordObject(toggle, "Toggle UI"); // F1: record the serialized state change
                toggle.isOn = !toggle.isOn;
                return true;
            }

            // Find dropdown component
            var dropdown = target.GetComponent<Dropdown>();
            if (dropdown != null && dropdown.interactable)
            {
                dropdown.Show();
                return true;
            }

            // For other selectables, try to invoke submit event
            var selectable = target.GetComponent<Selectable>();
            if (selectable != null && selectable.interactable)
            {
                var pointer = new PointerEventData(EventSystem.current);
                ExecuteEvents.Execute(target, pointer, ExecuteEvents.submitHandler);
                return true;
            }

            return false;
        }

        private static object GetElementState(GameObject target, bool includeChildren, bool includeInteractableInfo)
        {
            var state = new Dictionary<string, object>
            {
                ["path"] = GetGameObjectPath(target),
                ["name"] = target.name,
                ["isActive"] = target.activeInHierarchy,
                ["tag"] = target.tag,
                ["layer"] = LayerMask.LayerToName(target.layer)
            };

            // Get component information
            var components = new List<string>();
            foreach (var component in target.GetComponents<Component>())
            {
                components.Add(component.GetType().Name);
            }
            state["components"] = components;

            // Get UI-specific state
            var graphic = target.GetComponent<Graphic>();
            if (graphic != null)
            {
                state["color"] = ColorToHex(graphic.color);
                state["raycastTarget"] = graphic.raycastTarget;
            }

            if (includeInteractableInfo)
            {
                // GetComponent<Component>() returns the Transform (always first), so the
                // old call never saw the UI component — isInteractable was always just
                // activeInHierarchy. Resolve the actual Selectable (Button/Toggle/Slider…).
                var selectable = target.GetComponent<Selectable>();
                state["isInteractable"] = selectable != null ? selectable.interactable : target.activeInHierarchy;
                
                // Get specific UI element values
                var inputField = target.GetComponent<InputField>();
                if (inputField != null)
                {
                    state["value"] = inputField.text;
                    state["placeholder"] = inputField.placeholder?.GetComponent<Text>()?.text;
                }

                var toggle = target.GetComponent<Toggle>();
                if (toggle != null)
                {
                    state["isOn"] = toggle.isOn;
                }

                var slider = target.GetComponent<Slider>();
                if (slider != null)
                {
                    state["value"] = slider.value;
                    state["minValue"] = slider.minValue;
                    state["maxValue"] = slider.maxValue;
                }

                var dropdown = target.GetComponent<Dropdown>();
                if (dropdown != null)
                {
                    state["value"] = dropdown.value;
                    state["options"] = dropdown.options.Select(o => o.text).ToList();
                }

                var text = target.GetComponent<Text>();
                if (text != null)
                {
                    state["text"] = text.text;
                    state["fontSize"] = text.fontSize;
                    state["font"] = text.font?.name;
                }
            }

            if (includeChildren)
            {
                var children = new List<object>();
                foreach (Transform child in target.transform)
                {
                    children.Add(GetElementState(child.gameObject, false, includeInteractableInfo));
                }
                state["children"] = children;
            }

            return state;
        }

        private static bool SetElementValue(GameObject target, JToken value, bool triggerEvents)
        {
            // Handle InputField
            var inputField = target.GetComponent<InputField>();
            if (inputField != null)
            {
                Undo.RecordObject(inputField, "Set UI Value"); // F1: record the serialized state change
                inputField.text = value.ToString();
                if (triggerEvents)
                {
                    inputField.onValueChanged.Invoke(inputField.text);
                    inputField.onEndEdit.Invoke(inputField.text);
                }
                return true;
            }

            // Handle Toggle
            var toggle = target.GetComponent<Toggle>();
            if (toggle != null)
            {
                Undo.RecordObject(toggle, "Set UI Value"); // F1: record the serialized state change
                toggle.isOn = value.ToObject<bool>();
                if (triggerEvents)
                {
                    toggle.onValueChanged.Invoke(toggle.isOn);
                }
                return true;
            }

            // Handle Slider
            var slider = target.GetComponent<Slider>();
            if (slider != null)
            {
                Undo.RecordObject(slider, "Set UI Value"); // F1: record the serialized state change
                slider.value = value.ToObject<float>();
                if (triggerEvents)
                {
                    slider.onValueChanged.Invoke(slider.value);
                }
                return true;
            }

            // Handle Dropdown
            var dropdown = target.GetComponent<Dropdown>();
            if (dropdown != null)
            {
                dropdown.value = value.ToObject<int>();
                if (triggerEvents)
                {
                    dropdown.onValueChanged.Invoke(dropdown.value);
                }
                return true;
            }

            // Handle Text
            var text = target.GetComponent<Text>();
            if (text != null)
            {
                text.text = value.ToString();
                return true;
            }

            return false;
        }

        private static string ColorToHex(Color color)
        {
            return $"#{ColorUtility.ToHtmlStringRGBA(color)}";
        }

        #endregion
    }
}