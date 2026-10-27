# UI Toolkit workflows

[Workflow index](workflows.md). Read this reference only for the matching task.

> **Template:** Adapt object names, paths, component properties, and package-dependent values to the current project. Verify the template against the installed schema and project before use.

## UI Creation Workflows

Use the project's intended UI system. `manage_ui` operates on UI Toolkit assets and UIDocuments; [uGUI foundation](workflow-ugui-foundation.md) and [controls](workflow-ugui-controls.md) use Canvas components. Inspect project capabilities and existing UI before choosing or extending either.

Before mutations, use [setup/readiness](workflow-setup.md). Validate component/property names against the current project, resolve ambiguous targets by current instance ID or full path, and verify numeric enum placeholders.

### Step 0: Detect Project UI Capabilities

**Before creating any UI**, read project info to determine which packages and input system are available.

```python
# Inspect the data payload from mcpforunity://project/info:
# {
#   "renderPipeline": "BuiltIn" | "Universal" | "HighDefinition" | "Custom",
#   "activeInputHandler": "Old" | "New" | "Both",
#   "packages": {
#     "ugui": true/false,        — com.unity.ugui (Canvas, Image, Button, etc.)
#     "textmeshpro": true/false,  — com.unity.textmeshpro (TextMeshProUGUI)
#     "inputsystem": true/false,  — com.unity.inputsystem (new Input System)
#     "uiToolkit": true/false,    — reported UI Toolkit capability
#     "screenCapture": true/false  — ScreenCapture module enabled
#   }
# }
```

**Decision matrix:**

| project_info field | Value | What to use |
|---|---|---|
| `packages.uiToolkit` | `true` | UI Toolkit (UXML/USS) is available; use it when it matches the requested UI |
| `packages.ugui` | `true` | Canvas-based UI (Image, Button, etc.) via `batch_execute` |
| `packages.textmeshpro` | `true` | `TextMeshProUGUI` for text (uGUI) |
| `packages.textmeshpro` | `false` | `UnityEngine.UI.Text` (legacy, lower quality) |
| `activeInputHandler` | `"Old"` | `StandaloneInputModule` for EventSystem (uGUI) |
| `activeInputHandler` | `"New"` | `InputSystemUIInputModule` for EventSystem (uGUI) |
| `activeInputHandler` | `"Both"` | Either works; prefer `InputSystemUIInputModule` for UI |

### UI Toolkit Workflows (manage_ui)

UI Toolkit uses **UXML** for structure and **USS** for styling. Check the current package/version and existing screen conventions before adapting this template.

Use `<ui:Style>` with the namespace declaration shown in this template. A bare `<Style>` without a matching default namespace is not equivalent.

#### Create a Complete UI Screen

```python
# 1. Create USS stylesheet (styling)
manage_ui(
    action="create",
    path="Assets/UI/MainMenu.uss",
    contents='''.root-container {
    flex-grow: 1;
    justify-content: center;
    align-items: center;
    background-color: rgba(0, 0, 0, 0.8);
}
.title {
    font-size: 48px;
    color: white;
    -unity-font-style: bold;
    margin-bottom: 40px;
}
.menu-button {
    width: 300px;
    height: 60px;
    font-size: 24px;
    margin: 8px;
    background-color: rgb(50, 120, 200);
    color: white;
    border-radius: 8px;
}
.menu-button:hover {
    background-color: rgb(70, 140, 220);
}'''
)

# 2. Create UXML document (structure)
manage_ui(
    action="create",
    path="Assets/UI/MainMenu.uxml",
    contents='''<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements">
    <ui:Style src="MainMenu.uss" />
    <ui:VisualElement name="root" class="root-container">
        <ui:Label text="My Game" class="title" />
        <ui:Button text="Play" name="play-btn" class="menu-button" />
        <ui:Button text="Settings" name="settings-btn" class="menu-button" />
        <ui:Button text="Quit" name="quit-btn" class="menu-button" />
    </ui:VisualElement>
</ui:UXML>'''
)

# 3. Create a GameObject and attach UIDocument
manage_gameobject(action="create", name="UIRoot")
manage_ui(
    action="attach_ui_document",
    target="UIRoot",
    source_asset="Assets/UI/MainMenu.uxml"
    # panel_settings auto-created if omitted
)

# 4. Verify the visual tree
manage_ui(action="get_visual_tree", target="UIRoot", max_depth=5)
```

#### Update Existing UI

```python
# Read current content
result = manage_ui(action="read", path="Assets/UI/MainMenu.uss")
# Construct updated_complete_stylesheet from the full returned stylesheet.
# Change only the intended rule; update replaces the whole file.
manage_ui(
    action="update",
    path="Assets/UI/MainMenu.uss",
    contents=updated_complete_stylesheet
)
```

#### Custom PanelSettings

```python
# Create PanelSettings with ScaleWithScreenSize
manage_ui(
    action="create_panel_settings",
    path="Assets/UI/GamePanelSettings.asset",
    scale_mode="ScaleWithScreenSize",
    reference_resolution={"width": 1920, "height": 1080}
)

# Attach UIDocument with custom PanelSettings
manage_ui(
    action="attach_ui_document",
    target="UIRoot",
    source_asset="Assets/UI/MainMenu.uxml",
    panel_settings="Assets/UI/GamePanelSettings.asset"
)
```

## Verify and finish

Read back the UXML/USS and the UIDocument's asset assignments. Inspect `get_visual_tree`, Console import/style errors, and a screenshot at the intended resolution. This menu only creates structure and styling: bind application callbacks before claiming the buttons work. If this task created temporary objects/assets, clean up only those owned by the task; preserve the user's existing UI and save intentional changes.
