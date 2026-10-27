# uGUI controls and reference wiring

[Workflow index](workflows.md). Read this reference only for the matching task.

> **Template:** Adapt object names, paths, component properties, and package-dependent values to the current project. Verify the template against the installed schema and project before use.

Prerequisites: [uGUI foundation](workflow-ugui-foundation.md) is established, the target Canvas/panel exists, the input module matches the project, and TMP is installed for TMP examples. Inspect existing components before adding them. Names are placeholders; use verified current IDs or full paths where names are ambiguous. Each hierarchy and its reference-wiring phase form one procedure: finish both and inspect the resulting serialized references before claiming completion.

These native batches execute in order. `fail_fast=True` stops at the first failure without rolling back earlier changes; inspect outcomes before continuing. For cleanup, remove only objects this task created and still owns, after the runtime no longer holds them.

### Create Slider (With Reference Wiring)

A Slider requires a specific hierarchy and **must have its `fillRect` and `handleRect` references wired** to function.

```python
# Step 1: Create hierarchy
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "HealthSlider", "parent": "MainCanvas"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "HealthSlider", "component_type": "Slider"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "HealthSlider", "component_type": "Image"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "HealthSlider",
        "component_type": "RectTransform",
        "properties": {
            "anchorMin": [0.5, 0.5], "anchorMax": [0.5, 0.5],
            "sizeDelta": [400, 30], "anchoredPosition": [0, 0]
        }
    }},
    # Background
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "SliderBG", "parent": "HealthSlider"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "SliderBG", "component_type": "Image"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "SliderBG",
        "component_type": "Image", "property": "color", "value": [0.3, 0.3, 0.3, 1.0]
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "SliderBG",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [0, 0]}
    }},
    # Fill Area + Fill
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "FillArea", "parent": "HealthSlider"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "FillArea", "component_type": "RectTransform"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "FillArea",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [0, 0]}
    }},
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "SliderFill", "parent": "FillArea"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "SliderFill", "component_type": "Image"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "SliderFill",
        "component_type": "Image", "property": "color", "value": [0.2, 0.8, 0.2, 1.0]
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "SliderFill",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [0, 0]}
    }},
    # Handle Area + Handle
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "HandleArea", "parent": "HealthSlider"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "HandleArea", "component_type": "RectTransform"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "HandleArea",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [0, 0]}
    }},
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "SliderHandle", "parent": "HandleArea"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "SliderHandle", "component_type": "Image"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "SliderHandle",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0.5, 0], "anchorMax": [0.5, 1], "sizeDelta": [20, 0]}
    }}
])

# Step 2: Wire Slider references (CRITICAL — slider won't work without this)
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "HealthSlider",
        "component_type": "Slider", "property": "fillRect",
        "value": {"name": "SliderFill"}
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "HealthSlider",
        "component_type": "Slider", "property": "handleRect",
        "value": {"name": "SliderHandle"}
    }}
])
```

### Create Input Field (With Reference Wiring)

```python
# Step 1: Create hierarchy
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "NameInput", "parent": "MenuPanel"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "NameInput", "component_type": "Image"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "NameInput",
        "component_type": "TMP_InputField"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "NameInput",
        "component_type": "RectTransform",
        "properties": {
            "anchorMin": [0.5, 0.5], "anchorMax": [0.5, 0.5],
            "sizeDelta": [400, 50], "anchoredPosition": [0, 0]
        }
    }},
    # Text Area child (clips text to input bounds)
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "InputTextArea", "parent": "NameInput"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "InputTextArea", "component_type": "RectMask2D"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "InputTextArea",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [-16, -8]}
    }},
    # Placeholder
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "InputPlaceholder", "parent": "InputTextArea"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "InputPlaceholder", "component_type": "TextMeshProUGUI"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "InputPlaceholder",
        "component_type": "TextMeshProUGUI",
        "properties": {"text": "Enter name...", "fontStyle": 2, "color": [0.5, 0.5, 0.5, 0.5]}
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "InputPlaceholder",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [0, 0]}
    }},
    # Actual text display
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "InputText", "parent": "InputTextArea"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "InputText", "component_type": "TextMeshProUGUI"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "InputText",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [0, 0]}
    }}
])

# Step 2: Wire TMP_InputField references (CRITICAL)
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "NameInput",
        "component_type": "TMP_InputField", "property": "textViewport",
        "value": {"name": "InputTextArea"}
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "NameInput",
        "component_type": "TMP_InputField", "property": "textComponent",
        "value": {"name": "InputText"}
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "NameInput",
        "component_type": "TMP_InputField", "property": "placeholder",
        "value": {"name": "InputPlaceholder"}
    }}
])
```

### Create Toggle (With Reference Wiring)

```python
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "SoundToggle", "parent": "MenuPanel"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "SoundToggle", "component_type": "Toggle"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "SoundToggle",
        "component_type": "RectTransform",
        "properties": {
            "anchorMin": [0.5, 0.5], "anchorMax": [0.5, 0.5],
            "sizeDelta": [200, 30], "anchoredPosition": [0, 0]
        }
    }},
    # Background box
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "ToggleBG", "parent": "SoundToggle"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "ToggleBG", "component_type": "Image"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "ToggleBG",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0, 0.5], "anchorMax": [0, 0.5], "sizeDelta": [26, 26], "anchoredPosition": [13, 0]}
    }},
    # Checkmark
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "ToggleCheckmark", "parent": "ToggleBG"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "ToggleCheckmark", "component_type": "Image"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "ToggleCheckmark",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0.1, 0.1], "anchorMax": [0.9, 0.9], "sizeDelta": [0, 0]}
    }},
    # Label
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "ToggleLabel", "parent": "SoundToggle"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "ToggleLabel", "component_type": "TextMeshProUGUI"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "ToggleLabel",
        "component_type": "TextMeshProUGUI",
        "properties": {"text": "Sound Effects", "fontSize": 18, "alignment": 513}
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "ToggleLabel",
        "component_type": "RectTransform",
        "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [-35, 0], "anchoredPosition": [17.5, 0]}
    }}
])

# Wire Toggle references (CRITICAL)
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "SoundToggle",
        "component_type": "Toggle", "property": "graphic",
        "value": {"name": "ToggleCheckmark"}
    }}
])
```

## Verify the selected control

Read the configured component after wiring; confirm each field resolves to the intended component type, not merely to an object with the same name. Inspect anchors and dimensions, capture the screen, and verify value/input callbacks in the project's permitted runtime flow. Preserve the original environment and save intentional changes. A successful setter or screenshot does not establish working interaction.
