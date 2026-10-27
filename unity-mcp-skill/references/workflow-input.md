# Input system workflows

[Workflow index](workflows.md). Read this reference only for the matching task.

> **Template:** Adapt object names, paths, component properties, and package-dependent values to the current project. Verify the template against the installed schema and project before use.

Before creating an EventSystem, inspect existing systems and reuse the intended active one. Run only the branch matching the observed project configuration. Changing project input settings or installing packages is a separate mutation within the user's requested scope. After script edits, use [the script completion procedure](workflow-scripts.md); verify actual UI/gameplay behavior before concluding input works.

## Input System: Old vs New

Unity has two input systems that affect UI interaction, script input handling, and EventSystem configuration. **Always check `project_info.activeInputHandler` before creating EventSystems or writing input code.**

### Detection

```python
# Read mcpforunity://project/info
# activeInputHandler: "Old" | "New" | "Both"
# packages.inputsystem: true/false (whether com.unity.inputsystem is installed)
```

### EventSystem — Old Input Manager

Used when `activeInputHandler` is `"Old"`. Uses `StandaloneInputModule` which reads from `Input.GetAxis()` / `Input.GetButton()`.

```python
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "EventSystem"}},
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

Script fragment for the old Input Manager; supply the project's `speed`, `Jump()`, and `Fire()` members:

```csharp
// Input.GetAxis / Input.GetKey — works with old Input Manager
void Update()
{
    float h = Input.GetAxis("Horizontal");
    float v = Input.GetAxis("Vertical");
    transform.Translate(new Vector3(h, 0, v) * speed * Time.deltaTime);

    if (Input.GetKeyDown(KeyCode.Space))
        Jump();

    if (Input.GetMouseButtonDown(0))
        Fire();
}
```

### EventSystem — New Input System

Used when `activeInputHandler` is `"New"` or `"Both"`. Uses `InputSystemUIInputModule` from the `com.unity.inputsystem` package.

```python
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_gameobject", "params": {"action": "create", "name": "EventSystem"}},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "EventSystem",
        "component_type": "UnityEngine.EventSystems.EventSystem"
    }},
    {"tool": "manage_components", "params": {
        "action": "add", "target": "EventSystem",
        "component_type": "UnityEngine.InputSystem.UI.InputSystemUIInputModule"
    }}
])
```

Script template for a configured `PlayerInput` component using Send Messages. Supply the project's `Jump()` implementation and actions asset; this fragment is not a complete gameplay controller:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public float speed = 5f;
    private Vector2 moveInput;

    // Configure PlayerInput with an actions asset and Send Messages notification.
    // Unity Events use a different callback signature; inspect that API separately.
    public void OnMove(InputValue value)
    {
        moveInput = value.Get<Vector2>();
    }

    public void OnJump(InputValue value)
    {
        if (value.isPressed)
            Jump();
    }

    void Update()
    {
        Vector3 move = new Vector3(moveInput.x, 0, moveInput.y);
        transform.Translate(move * speed * Time.deltaTime);
    }
}
```

### When `activeInputHandler` is `"Both"`

Both systems are active simultaneously. For UI, prefer `InputSystemUIInputModule`. For gameplay scripts, either approach works — `Input.GetAxis()` still functions alongside the new Input System.

```python
# UI: use new Input System module
{"tool": "manage_components", "params": {
    "action": "add", "target": "EventSystem",
    "component_type": "UnityEngine.InputSystem.UI.InputSystemUIInputModule"
}}

# Gameplay scripts: Input.GetAxis() still works in "Both" mode
# But prefer the new Input System for consistency
```

> **Gotcha:** Adding `StandaloneInputModule` when `activeInputHandler` is `"New"` will cause a runtime error. Always check first.
