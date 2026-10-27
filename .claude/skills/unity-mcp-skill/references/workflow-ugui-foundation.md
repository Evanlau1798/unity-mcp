# uGUI foundation and screen composition

[Workflow index](workflows.md). Read this reference only for the matching task.

> **Template:** Adapt object names, paths, component properties, and package-dependent values to the current project. Verify the template against the installed schema and project before use.

Before mutations, use [setup and readiness](workflow-setup.md). Read `mcpforunity://project/info`: confirm uGUI availability, identify TMP support, and choose the EventSystem input module from `activeInputHandler` plus `packages.inputsystem`. Inspect existing Canvas/EventSystem objects before creating new ones. Read only [the needed control procedure](workflow-ugui-controls.md) when adding a Slider, input field, or Toggle.

All examples use unique placeholder names. Resolve ambiguous targets to current instance IDs or full hierarchy paths. Inspect component/property names and numeric enums for the installed packages. Dependent batches set `fail_fast=True`; inspect each result before starting the next phase, because a failure leaves earlier changes in place.

### uGUI (Canvas-Based) Workflows

The sections below cover legacy Canvas-based UI using `batch_execute`. Use these when working with existing uGUI projects or when UI Toolkit is not suitable.

### RectTransform Sizing (Critical for All UI Children)

UI elements need a `RectTransform` with deliberate anchors and dimensions. Parenting an empty GameObject beneath a Canvas does not by itself establish a correctly configured UI container. Inspect the component first; add `RectTransform` to empty containers when needed. Set anchor/size before judging visibility:

```python
# Stretch to fill parent (common for panels/backgrounds)
{"tool": "manage_components", "params": {
    "action": "set_property", "target": "MyPanel",
    "component_type": "RectTransform",
    "properties": {
        "anchorMin": [0, 0],        # bottom-left corner
        "anchorMax": [1, 1],        # top-right corner
        "sizeDelta": [0, 0],        # no extra size beyond anchors
        "anchoredPosition": [0, 0]  # centered on anchors
    }
}}

# Fixed-size centered element (e.g. 300x50 button)
{"tool": "manage_components", "params": {
    "action": "set_property", "target": "MyButton",
    "component_type": "RectTransform",
    "properties": {
        "anchorMin": [0.5, 0.5],
        "anchorMax": [0.5, 0.5],
        "sizeDelta": [300, 50],
        "anchoredPosition": [0, 0]
    }
}}

# Top-anchored bar (e.g. health bar at top of screen)
{"tool": "manage_components", "params": {
    "action": "set_property", "target": "TopBar",
    "component_type": "RectTransform",
    "properties": {
        "anchorMin": [0, 1],        # left-top
        "anchorMax": [1, 1],        # right-top (stretch horizontally)
        "sizeDelta": [0, 60],       # 60px tall, full width
        "anchoredPosition": [0, -30] # offset down by half height
    }
}}
```

> **Note:** Vector2 properties accept both `[x, y]` array format and `{"x": ..., "y": ...}` object format.

### Create Canvas (Foundation for All UI)

For this screen-space interactive uGUI template, use a Canvas, CanvasScaler, and GraphicRaycaster. Canvas is the rendering root; the scaler and raycaster serve scaling and input needs. Reuse an appropriate existing Canvas when present.

```python
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "MainCanvas"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "MainCanvas", "component_type": "Canvas"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "MainCanvas", "component_type": "CanvasScaler"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "MainCanvas", "component_type": "GraphicRaycaster"
    }},
    # renderMode: 0=ScreenSpaceOverlay, 1=ScreenSpaceCamera, 2=WorldSpace
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "MainCanvas",
        "component_type": "Canvas", "property": "renderMode", "value": 0
    }},
    # CanvasScaler: uiScaleMode 1=ScaleWithScreenSize
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "MainCanvas",
        "component_type": "CanvasScaler", "property": "uiScaleMode", "value": 1
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "MainCanvas",
        "component_type": "CanvasScaler", "property": "referenceResolution",
        "value": [1920, 1080]
    }}
])
```

### Create EventSystem (Required Once Per Scene for UI Interaction)

Inspect the scene for an existing active EventSystem before creating one. Choose exactly one branch below using the observed `project_info.activeInputHandler` and package availability; do not run both branches. For the complete input patterns, see [Input System](workflow-input.md).

```python
# For activeInputHandler == "New" or "Both" (project has Input System package):
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "EventSystem"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "EventSystem",
        "component_type": "UnityEngine.EventSystems.EventSystem"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "EventSystem",
        "component_type": "UnityEngine.InputSystem.UI.InputSystemUIInputModule"
    }}
])

# For activeInputHandler == "Old" (legacy Input Manager only):
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "EventSystem"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "EventSystem",
        "component_type": "UnityEngine.EventSystems.EventSystem"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "EventSystem",
        "component_type": "UnityEngine.EventSystems.StandaloneInputModule"
    }}
])
```

### Create Panel (Background Container)

A Panel is an Image component used as a background/container for other UI elements.

```python
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "MenuPanel", "parent": "MainCanvas"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "MenuPanel", "component_type": "Image"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "MenuPanel",
        "component_type": "Image", "property": "color",
        "value": [0.1, 0.1, 0.1, 0.8]
    }},
    # Size the panel (stretch to 60% of canvas, centered)
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "MenuPanel",
        "component_type": "RectTransform",
        "properties": {
            "anchorMin": [0.2, 0.1], "anchorMax": [0.8, 0.9],
            "sizeDelta": [0, 0], "anchoredPosition": [0, 0]
        }
    }}
])
```

### Create Text (TextMeshPro)

TextMeshProUGUI automatically adds a RectTransform when added to a child of a Canvas. If `packages.textmeshpro` is `false`, use `UnityEngine.UI.Text` instead.

```python
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "TitleText", "parent": "MenuPanel"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "TitleText",
        "component_type": "TextMeshProUGUI"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "TitleText",
        "component_type": "TextMeshProUGUI",
        "properties": {
            "text": "My Game Title",
            "fontSize": 48,
            "alignment": 514,
            "color": [1, 1, 1, 1]
        }
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "TitleText",
        "component_type": "RectTransform",
        "properties": {
            "anchorMin": [0, 0.8], "anchorMax": [1, 1],
            "sizeDelta": [0, 0], "anchoredPosition": [0, 0]
        }
    }}
])
```

> **TextMeshPro alignment values:** 257=TopLeft, 258=TopCenter, 260=TopRight, 513=MiddleLeft, 514=MiddleCenter, 516=MiddleRight, 1025=BottomLeft, 1026=BottomCenter, 1028=BottomRight.

### Create Button (With Label)

A Button needs an `Image` (visual) + `Button` (interaction) on the parent, and a child with `TextMeshProUGUI` for the label.

```python
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "StartButton", "parent": "MenuPanel"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "StartButton", "component_type": "Image"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "StartButton", "component_type": "Button"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "StartButton",
        "component_type": "Image", "property": "color",
        "value": [0.2, 0.6, 1.0, 1.0]
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "StartButton",
        "component_type": "RectTransform",
        "properties": {
            "anchorMin": [0.5, 0.5], "anchorMax": [0.5, 0.5],
            "sizeDelta": [300, 60], "anchoredPosition": [0, 0]
        }
    }},
    # Child text label (stretches to fill button)
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "StartButton_Label", "parent": "StartButton"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "StartButton_Label",
        "component_type": "TextMeshProUGUI"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "StartButton_Label",
        "component_type": "TextMeshProUGUI",
        "properties": {"text": "Start Game", "fontSize": 24, "alignment": 514}
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "StartButton_Label",
        "component_type": "RectTransform",
        "properties": {
            "anchorMin": [0, 0], "anchorMax": [1, 1],
            "sizeDelta": [0, 0], "anchoredPosition": [0, 0]
        }
    }}
])
```

### Add Layout Group (Vertical/Horizontal/Grid)

Layout groups arrange child RectTransforms. Supply suitable preferred/minimum sizes (for example through LayoutElement) and inspect which axes the group controls; an empty preferred height can still collapse a button. Avoid combining a layout group and ContentSizeFitter on conflicting axes.

```python
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_components", "params": {
        "action": "add", "target": "MenuPanel",
        "component_type": "VerticalLayoutGroup"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "MenuPanel",
        "component_type": "VerticalLayoutGroup",
        "properties": {
            "spacing": 10,
            "childAlignment": 4,
            "childForceExpandWidth": True,
            "childForceExpandHeight": False,
            "childControlWidth": True, "childControlHeight": True,
            "padding": {"left": 20, "right": 20, "top": 20, "bottom": 20}
        }
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "MenuPanel",
        "component_type": "ContentSizeFitter"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "MenuPanel",
        "component_type": "ContentSizeFitter",
        "properties": { "verticalFit": 2 }
    }}
])
```

> **childAlignment values:** 0=UpperLeft, 1=UpperCenter, 2=UpperRight, 3=MiddleLeft, 4=MiddleCenter, 5=MiddleRight, 6=LowerLeft, 7=LowerCenter, 8=LowerRight.
> **ContentSizeFitter fit modes:** 0=Unconstrained, 1=MinSize, 2=PreferredSize.

### Complete Example: Main Menu Screen

Combines the templates into a menu layout. Prerequisites: the example names are unused, no EventSystem needs reuse, and `project_info` is the inspected project-info payload. The default batch cap is 25, configurable from 1–100 with a hard maximum of 100. The three phases below each fit the default cap. Inspect each phase before proceeding; these calls do not provide rollback. Button callbacks are project code and must be wired separately before claiming interaction works.

```python
# Choose from the observed project configuration before running a batch.
if project_info["activeInputHandler"] == "New" and not project_info["packages"]["inputsystem"]:
    raise RuntimeError("Resolve the New-input package prerequisite before creating UI")
input_module = (
    "UnityEngine.InputSystem.UI.InputSystemUIInputModule"
    if project_info["activeInputHandler"] in ("New", "Both")
    and project_info["packages"]["inputsystem"]
    else "UnityEngine.EventSystems.StandaloneInputModule"
)
# If handler is New but the package is absent, resolve that prerequisite first.

# Batch 1: Canvas + EventSystem + Panel + Title
batch_execute(fail_fast=True, commands=[
    # Canvas
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "MenuCanvas"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "MenuCanvas", "component_type": "Canvas"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "MenuCanvas", "component_type": "CanvasScaler"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "MenuCanvas", "component_type": "GraphicRaycaster"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "MenuCanvas", "component_type": "Canvas", "property": "renderMode", "value": 0}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "MenuCanvas", "component_type": "CanvasScaler", "properties": {"uiScaleMode": 1, "referenceResolution": [1920, 1080]}}},
    # EventSystem — use StandaloneInputModule OR InputSystemUIInputModule based on project_info
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "EventSystem"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "EventSystem", "component_type": "UnityEngine.EventSystems.EventSystem"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "EventSystem", "component_type": input_module}},
    # Panel (centered, 60% width)
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "MenuPanel", "parent": "MenuCanvas"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "MenuPanel", "component_type": "Image"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "MenuPanel", "component_type": "Image", "property": "color", "value": [0.1, 0.1, 0.15, 0.9]}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "MenuPanel", "component_type": "RectTransform", "properties": {"anchorMin": [0.2, 0.15], "anchorMax": [0.8, 0.85], "sizeDelta": [0, 0]}}},
    {"tool": "manage_components", "params": {"action": "add", "target": "MenuPanel", "component_type": "VerticalLayoutGroup"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "MenuPanel", "component_type": "VerticalLayoutGroup", "properties": {"spacing": 20, "childAlignment": 4, "childForceExpandWidth": True, "childForceExpandHeight": False, "childControlWidth": True, "childControlHeight": True}}},
    # Title
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "Title", "parent": "MenuPanel"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "Title", "component_type": "TextMeshProUGUI"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "Title", "component_type": "TextMeshProUGUI", "properties": {"text": "My Game", "fontSize": 64, "alignment": 514, "color": [1, 1, 1, 1]}}}
])

# Batch 2: Buttons
batch_execute(fail_fast=True, commands=[
    # Play Button
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "PlayButton", "parent": "MenuPanel"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "PlayButton", "component_type": "Image"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "PlayButton", "component_type": "Button"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "PlayButton", "component_type": "Image", "property": "color", "value": [0.2, 0.6, 1.0, 1.0]}},
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "PlayLabel", "parent": "PlayButton"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "PlayLabel", "component_type": "TextMeshProUGUI"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "PlayLabel", "component_type": "TextMeshProUGUI", "properties": {"text": "Play", "fontSize": 32, "alignment": 514}}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "PlayLabel", "component_type": "RectTransform", "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [0, 0]}}},
    # Settings Button
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "SettingsButton", "parent": "MenuPanel"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "SettingsButton", "component_type": "Image"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "SettingsButton", "component_type": "Button"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "SettingsButton", "component_type": "Image", "property": "color", "value": [0.3, 0.3, 0.35, 1.0]}},
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "SettingsLabel", "parent": "SettingsButton"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "SettingsLabel", "component_type": "TextMeshProUGUI"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "SettingsLabel", "component_type": "TextMeshProUGUI", "properties": {"text": "Settings", "fontSize": 32, "alignment": 514}}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "SettingsLabel", "component_type": "RectTransform", "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [0, 0]}}},
    # Quit Button
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "QuitButton", "parent": "MenuPanel"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "QuitButton", "component_type": "Image"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "QuitButton", "component_type": "Button"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "QuitButton", "component_type": "Image", "property": "color", "value": [0.8, 0.2, 0.2, 1.0]}},
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "QuitLabel", "parent": "QuitButton"}},
    {"tool": "manage_components", "params": {"action": "add", "target": "QuitLabel", "component_type": "TextMeshProUGUI"}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "QuitLabel", "component_type": "TextMeshProUGUI", "properties": {"text": "Quit", "fontSize": 32, "alignment": 514}}},
    {"tool": "manage_components", "params": {"action": "set_property", "target": "QuitLabel", "component_type": "RectTransform", "properties": {"anchorMin": [0, 0], "anchorMax": [1, 1], "sizeDelta": [0, 0]}}}
])
```

After creating the menu, supply layout sizes in a third phase. Adapt these dimensions to the design; retain any existing LayoutElement instead of adding duplicates.

```python
layout_commands = []
for element, height in [("Title", 100), ("PlayButton", 60),
                        ("SettingsButton", 60), ("QuitButton", 60)]:
    layout_commands.extend([
        {"tool": "manage_components", "params": {
            "action": "add", "target": element, "component_type": "LayoutElement"}},
        {"tool": "manage_components", "params": {
            "action": "set_property", "target": element,
            "component_type": "LayoutElement", "properties": {"preferredHeight": height}}}
    ])
batch_execute(commands=layout_commands, fail_fast=True)
```

Inspect RectTransforms and serialized references, capture the screen, then exercise callbacks under the project's permitted runtime workflow. Save intentional scene changes. Visual appearance alone does not verify interaction.

### UI Component Quick Reference

| UI Element | Required Components | Notes |
| ---------- | ------------------- | ----- |
| **Canvas** | Canvas + CanvasScaler + GraphicRaycaster | Rendering root; reuse or add according to the screen design. |
| **EventSystem** | EventSystem + input module (see below) | Reuse the intended active EventSystem; avoid accidental duplicates. |
| **Panel** | Image + RectTransform sizing | Container. Set color for background. |
| **Text** | TextMeshProUGUI (or Text if no TMP) + RectTransform | Check `packages.textmeshpro`. |
| **Button** | Image + Button + child(TextMeshProUGUI) + RectTransform | Image = visual, Button = click handler. |
| **Slider** | Slider + Image + children + **wire fillRect/handleRect** | Won't function without wiring. |
| **Toggle** | Toggle + children + **wire graphic** | Wire checkmark Image to `graphic`. |
| **Input Field** | Image + TMP_InputField + children + **wire textViewport/textComponent/placeholder** | Won't function without wiring. |
| **Layout Group** | VerticalLayoutGroup / HorizontalLayoutGroup / GridLayoutGroup | Arranges child RectTransforms; provide valid size inputs. |
