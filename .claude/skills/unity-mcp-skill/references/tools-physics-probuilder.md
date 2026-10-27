# Unity-MCP Physics / ProBuilder Tools

Read only the sections needed for the current task. Return to the [tool index](tools-reference.md) for other categories.

All examples are **templates**, not evidence of a live run. Use the installed tool schema and the target project/package versions as authority; native batch commands can differ from their Python wrappers.

## Physics Tools

### `manage_physics`

Manage 3D and 2D physics: settings, collision matrix, materials, joints, queries, validation, and simulation. All actions support `dimension="3d"` (default) or `dimension="2d"` where applicable.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | See action groups below |
| `dimension` | string | No | `"3d"` (default) or `"2d"` |
| `settings` | object | For set_settings | Key-value physics settings dict |
| `layer_a` / `layer_b` | string | For collision matrix | Layer name or index |
| `collide` | bool | For set_collision_matrix | `true` to enable, `false` to disable |
| `name` | string | For create_physics_material | Material asset name |
| `path` | string | No | Asset folder path (create) or asset path (configure) |
| `dynamic_friction` / `static_friction` / `bounciness` | float | No | Material properties (0–1) |
| `friction_combine` / `bounce_combine` | string | No | `Average`, `Minimum`, `Multiply`, `Maximum` |
| `material_path` | string | For assign_physics_material | Path to physics material asset |
| `target` | string | For joints/queries/validate | GameObject name or instance ID |
| `joint_type` | string | For joints | 3D: `fixed`, `hinge`, `spring`, `character`, `configurable`; 2D: `distance`, `fixed`, `friction`, `hinge`, `relative`, `slider`, `spring`, `target`, `wheel` |
| `connected_body` | string | For add_joint | Connected body GameObject |
| `motor` / `limits` / `spring` / `drive` | object | For configure_joint | Joint sub-config objects |
| `properties` | object | For configure_joint/material | Direct property dict |
| `origin` / `direction` | float[] | For raycast | Ray origin and direction `[x,y,z]` or `[x,y]` |
| `max_distance` | float | No | Max raycast distance |
| `shape` | string | For overlap | `sphere`, `box`, `capsule` (3D); `circle`, `box`, `capsule` (2D) |
| `position` | float[] | For overlap | `[x,y,z]` or `[x,y]` |
| `size` | float or float[] | For overlap | Radius (sphere/circle) or half-extents `[x,y,z]` (box) |
| `layer_mask` | string | No | Layer name or int mask for queries |
| `start` / `end` | float[] | For linecast | Start and end points `[x,y,z]` or `[x,y]` |
| `point1` / `point2` | float[] | For shapecast capsule | Capsule endpoints (3D alternative) |
| `height` | float | For shapecast capsule | Capsule height |
| `capsule_direction` | int | For shapecast capsule | 0=X, 1=Y (default), 2=Z |
| `angle` | float | For 2D shapecasts | Rotation angle in degrees |
| `force` | float[] | For apply_force | Force vector `[x,y,z]` or `[x,y]` |
| `force_mode` | string | For apply_force | `Force`, `Impulse`, `Acceleration`, `VelocityChange` (3D); `Force`, `Impulse` (2D) |
| `force_type` | string | For apply_force | `normal` (default) or `explosion` (3D only) |
| `torque` | float[] | For apply_force | Torque `[x,y,z]` (3D) or `[z]` (2D) |
| `explosion_position` | float[] | For apply_force explosion | Explosion center `[x,y,z]` |
| `explosion_radius` | float | For apply_force explosion | Explosion sphere radius |
| `explosion_force` | float | For apply_force explosion | Explosion force magnitude |
| `upwards_modifier` | float | For apply_force explosion | Y-axis offset (default 0) |
| `steps` | int | For simulate_step | Number of steps (1–100) |
| `step_size` | float | No | Step size in seconds (default: `Time.fixedDeltaTime`) |

**Action groups:**

- **Settings:** `ping`, `get_settings`, `set_settings`
- **Collision Matrix:** `get_collision_matrix`, `set_collision_matrix`
- **Materials:** `create_physics_material`, `configure_physics_material`, `assign_physics_material`
- **Joints:** `add_joint`, `configure_joint`, `remove_joint`
- **Queries:** `raycast`, `raycast_all`, `linecast`, `shapecast`, `overlap`
- **Forces:** `apply_force`
- **Rigidbody:** `get_rigidbody`, `configure_rigidbody`
- **Validation:** `validate`
- **Simulation:** `simulate_step`

```python
# Check physics status
manage_physics(action="ping")

# Get/set gravity
manage_physics(action="get_settings", dimension="3d")
manage_physics(action="set_settings", dimension="3d", settings={"gravity": [0, -20, 0]})

# Collision matrix
manage_physics(action="get_collision_matrix")
manage_physics(action="set_collision_matrix", layer_a="Player", layer_b="Enemy", collide=False)

# Create a bouncy physics material and assign it
manage_physics(action="create_physics_material", name="Bouncy", bounciness=0.9, dynamic_friction=0.2)
manage_physics(action="assign_physics_material", target="Ball", material_path="Assets/Physics Materials/Bouncy.physicMaterial")

# Add and configure a hinge joint
manage_physics(action="add_joint", target="Door", joint_type="hinge", connected_body="DoorFrame")
manage_physics(action="configure_joint", target="Door", joint_type="hinge",
               motor={"targetVelocity": 90, "force": 100},
               limits={"min": -90, "max": 0, "bounciness": 0})

# Raycast and overlap
manage_physics(action="raycast", origin=[0, 10, 0], direction=[0, -1, 0], max_distance=50)
manage_physics(action="overlap", shape="sphere", position=[0, 0, 0], size=5.0)

# Validate scene physics setup
manage_physics(action="validate")                    # whole scene
manage_physics(action="validate", target="Player")  # single object

# Multi-hit raycast (returns all hits sorted by distance)
manage_physics(action="raycast_all", origin=[0, 10, 0], direction=[0, -1, 0])

# Linecast (point A to point B)
manage_physics(action="linecast", start=[0, 0, 0], end=[10, 0, 0])

# Shapecast (sphere/box/capsule sweep)
manage_physics(action="shapecast", shape="sphere", origin=[0, 5, 0], direction=[0, -1, 0], size=0.5)
manage_physics(action="shapecast", shape="box", origin=[0, 5, 0], direction=[0, -1, 0], size=[1, 1, 1])

# Apply force (works with simulate_step for edit-mode previewing)
manage_physics(action="apply_force", target="Ball", force=[0, 500, 0], force_mode="Impulse")
manage_physics(action="apply_force", target="Ball", torque=[0, 10, 0])

# Explosion force (3D only)
manage_physics(action="apply_force", target="Crate", force_type="explosion",
               explosion_force=1000, explosion_position=[0, 0, 0], explosion_radius=10)

# Configure rigidbody properties
manage_physics(action="configure_rigidbody", target="Player",
               properties={"mass": 80, "drag": 0.5, "useGravity": True, "collisionDetectionMode": "Continuous"})

# Step physics in edit mode
manage_physics(action="simulate_step", steps=10, step_size=0.02)
```

---

## ProBuilder Tools

### manage_probuilder

Unified tool for ProBuilder mesh operations. Group: `probuilder`. Requires `com.unity.probuilder` package. When available, **prefer ProBuilder over primitive GameObjects** for editable geometry, multi-material faces, or complex shapes.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | Action to perform (see categories below) |
| `target` | string | Sometimes | Target GameObject name/path/id |
| `search_method` | string | No | How to find target: `by_id`, `by_name`, `by_path`, `by_tag`, `by_layer` |
| `properties` | dict \| string | No | Action-specific parameters (dict or JSON string) |

**Actions by category:**

**Shape Creation:**
- `create_shape` — Create ProBuilder primitive (shape_type, size, position, rotation, name). 12 types: Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Arch, Stair, CurvedStair, Door, Prism
- `create_poly_shape` — Create from 2D polygon footprint (points, extrudeHeight, flipNormals)

**Mesh Editing:**
- `extrude_faces` — Extrude faces (faceIndices, distance, method: FaceNormal/VertexNormal/IndividualFaces)
- `extrude_edges` — Extrude edges (edgeIndices or edges [{a,b},...], distance, asGroup)
- `bevel_edges` — Bevel edges (edgeIndices or edges [{a,b},...], amount 0-1)
- `subdivide` — Subdivide faces via ConnectElements (faceIndices optional)
- `delete_faces` — Delete faces (faceIndices)
- `bridge_edges` — Bridge two open edges (edgeA, edgeB as {a,b} pairs, allowNonManifold)
- `connect_elements` — Connect edges/faces (edgeIndices or faceIndices)
- `detach_faces` — Detach faces to new object (faceIndices, deleteSourceFaces)
- `flip_normals` — Flip face normals (faceIndices)
- `merge_faces` — Merge faces into one (faceIndices)
- `combine_meshes` — Combine ProBuilder objects (targets list)
- `merge_objects` — Merge objects with auto-convert (targets, name)
- `duplicate_and_flip` — Create double-sided geometry (faceIndices)
- `create_polygon` — Connect existing vertices into a new face (vertexIndices, unordered)

**Vertex Operations:**
- `merge_vertices` — Collapse vertices to single point (vertexIndices, collapseToFirst)
- `weld_vertices` — Weld vertices within proximity radius (vertexIndices, radius)
- `split_vertices` — Split shared vertices (vertexIndices)
- `move_vertices` — Translate vertices (vertexIndices, offset [x,y,z])
- `insert_vertex` — Insert vertex on edge or face (edge {a,b} or faceIndex + point [x,y,z])
- `append_vertices_to_edge` — Insert evenly-spaced points on edges (edgeIndices or edges, count)

**Selection:**
- `select_faces` — Select faces by criteria (direction + tolerance, growFrom + growAngle)

**UV & Materials:**
- `set_face_material` — Assign material to faces (faceIndices, materialPath)
- `set_face_color` — Set vertex color on faces (faceIndices, color [r,g,b,a])
- `set_face_uvs` — Set UV params (faceIndices, scale, offset, rotation, flipU, flipV)

**Query:**
- `get_mesh_info` — Get mesh details with `include` parameter:
  - `"summary"` (default): counts, bounds, materials
  - `"faces"`: + face normals, centers, and direction labels (capped at 100)
  - `"edges"`: + edge vertex pairs with world positions (capped at 200, deduplicated)
  - `"all"`: everything
- `ping` — Check if ProBuilder is available

**Smoothing:**
- `set_smoothing` — Set smoothing group on faces (faceIndices, smoothingGroup: 0=hard, 1+=smooth)
- `auto_smooth` — Auto-assign smoothing groups by angle (angleThreshold: default 30)

**Mesh Utilities:**
- `center_pivot` — Move pivot to mesh bounds center
- `freeze_transform` — Bake transform into vertices, reset transform
- `validate_mesh` — Check mesh health (read-only diagnostics)
- `repair_mesh` — Auto-fix degenerate triangles

**Reported compatibility issues:** Earlier guide notes report problems with `set_pivot` persistence and `convert_to_probuilder`/MeshImporter. This update did not verify those actions on the installed package version. See [limitations and alternatives](probuilder-guide.md#reported-compatibility-issues) before relying on them.

**Examples:**

```python
# Check availability
manage_probuilder(action="ping")

# Create a cube
manage_probuilder(action="create_shape", properties={"shape_type": "Cube", "name": "MyCube"})

# Get face info with directions
manage_probuilder(action="get_mesh_info", target="MyCube", properties={"include": "faces"})

# Extrude the top face (find it via direction="top" in get_mesh_info results)
manage_probuilder(action="extrude_faces", target="MyCube",
    properties={"faceIndices": [2], "distance": 1.5})

# Select all upward-facing faces
manage_probuilder(action="select_faces", target="MyCube",
    properties={"direction": "up", "tolerance": 0.7})

# Create double-sided geometry (for room interiors)
manage_probuilder(action="duplicate_and_flip", target="Room",
    properties={"faceIndices": [0, 1, 2, 3, 4, 5]})

# Weld nearby vertices
manage_probuilder(action="weld_vertices", target="MyCube",
    properties={"vertexIndices": [0, 1, 2, 3], "radius": 0.1})

# Auto-smooth
manage_probuilder(action="auto_smooth", target="MyCube", properties={"angleThreshold": 30})

# Cleanup workflow
manage_probuilder(action="center_pivot", target="MyCube")
manage_probuilder(action="validate_mesh", target="MyCube")
```

See also: [ProBuilder Workflow Guide](probuilder-guide.md) for detailed patterns and complex object examples.
