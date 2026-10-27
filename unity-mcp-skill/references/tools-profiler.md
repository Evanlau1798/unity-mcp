# Unity-MCP Profiler Tools

Read only the sections needed for the current task. Return to the [tool index](tools-reference.md) for other categories.

All examples are **templates**, not evidence of a live run. Use the installed tool schema and the target project/package versions as authority; native batch commands can differ from their Python wrappers.

## Profiler Tools

### `manage_profiler`

Unity Profiler session control, counter reads, memory snapshots, and Frame Debugger. Group: `profiling` (opt-in via `manage_tools`).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | See action groups below |
| `category` | string | For get_counters | Profiler category name (e.g. `Render`, `Scripts`, `Memory`, `Physics`) |
| `counters` | list[str] | No | Specific counter names for get_counters. Omit to read all in category |
| `object_path` | string | For get_object_memory | Scene hierarchy or asset path |
| `log_file` | string | No | Path to `.raw` file for profiler_start recording |
| `enable_callstacks` | bool | No | Enable allocation callstacks for profiler_start |
| `areas` | dict[str, bool] | For profiler_set_areas | Area name to enabled/disabled mapping |
| `snapshot_path` | string | No | Output path for memory_take_snapshot |
| `search_path` | string | No | Search directory for memory_list_snapshots |
| `snapshot_a` | string | For memory_compare_snapshots | First snapshot file path |
| `snapshot_b` | string | For memory_compare_snapshots | Second snapshot file path |
| `page_size` | int | No | Page size for frame_debugger_get_events (default 50) |
| `cursor` | int | No | Cursor offset for frame_debugger_get_events |

**Action groups:**

- **Session:** `profiler_start`, `profiler_stop`, `profiler_status`, `profiler_set_areas`
- **Counters:** `get_frame_timing`, `get_counters`, `get_object_memory`
- **Memory Snapshot:** `memory_take_snapshot`, `memory_list_snapshots`, `memory_compare_snapshots` (requires `com.unity.memoryprofiler`)
- **Frame Debugger:** `frame_debugger_enable`, `frame_debugger_disable`, `frame_debugger_get_events`
- **Utility:** `ping`

```python
# Check profiler availability
manage_profiler(action="ping")

# Start profiling (optionally record to file)
manage_profiler(action="profiler_start")
manage_profiler(action="profiler_start", log_file="Assets/profiler.raw", enable_callstacks=True)

# Check profiler status
manage_profiler(action="profiler_status")

# Toggle profiler areas
manage_profiler(action="profiler_set_areas", areas={"CPU": True, "GPU": True, "Rendering": True, "Memory": False})

# Stop profiling
manage_profiler(action="profiler_stop")

# Read frame timing data (12 fields from FrameTimingManager)
manage_profiler(action="get_frame_timing")

# Read counters by category
manage_profiler(action="get_counters", category="Render")
manage_profiler(action="get_counters", category="Memory", counters=["Total Used Memory", "GC Used Memory"])

# Get memory size of a specific object
manage_profiler(action="get_object_memory", object_path="Player/Mesh")

# Memory snapshots (requires com.unity.memoryprofiler)
manage_profiler(action="memory_take_snapshot")
manage_profiler(action="memory_take_snapshot", snapshot_path="Assets/Snapshots/baseline.snap")
manage_profiler(action="memory_list_snapshots")
manage_profiler(action="memory_compare_snapshots", snapshot_a="Assets/Snapshots/before.snap", snapshot_b="Assets/Snapshots/after.snap")

# Frame Debugger
manage_profiler(action="frame_debugger_enable")
manage_profiler(action="frame_debugger_get_events", page_size=20, cursor=0)
manage_profiler(action="frame_debugger_disable")
```
