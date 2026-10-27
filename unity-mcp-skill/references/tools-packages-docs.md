# Unity-MCP Package / Docs Tools

Read only the sections needed for the current task. Return to the [tool index](tools-reference.md) for other categories.

All examples are **templates**, not evidence of a live run. Use the installed tool schema and the target project/package versions as authority; native batch commands can differ from their Python wrappers.

## Package Tools

### manage_packages

Manage Unity packages: query, install, remove, embed, and configure registries. Install/remove trigger domain reload.

**Query Actions (read-only):**

| Action | Parameters | Description |
|--------|-----------|-------------|
| `list_packages` | — | List all installed packages (async, returns job_id) |
| `search_packages` | `query` | Search Unity registry by keyword (async, returns job_id) |
| `get_package_info` | `package` | Get details about a specific installed package |
| `list_registries` | — | List all scoped registries (names, URLs, scopes); immediate result |
| `ping` | — | Check package manager availability, Unity version, package count |
| `status` | `job_id` (required for list/search; optional for add/remove/embed) | Poll async job status; omit job_id to poll latest add/remove/embed job |

**Mutating Actions:**

| Action | Parameters | Description |
|--------|-----------|-------------|
| `add_package` | `package` | Install a package (name, name@version, git URL, or file: path) |
| `remove_package` | `package`, `force` (optional) | Remove a package; blocked if dependents exist unless `force=true` |
| `embed_package` | `package` | Copy package to local Packages/ for editing |
| `resolve_packages` | — | Force re-resolution of all packages |
| `add_registry` | `name`, `url`, `scopes` | Add a scoped registry (e.g., OpenUPM) |
| `remove_registry` | `name` or `url` | Remove a scoped registry |

**Input validation:**
- Valid package IDs: `com.unity.inputsystem`, `com.unity.cinemachine@3.1.6`
- Git URLs: allowed with warning ("ensure this is a trusted source")
- `file:` paths: allowed with warning
- Invalid names (uppercase, missing dots): rejected

**Example — List installed packages:**
```python
manage_packages(action="list_packages")
# Returns job_id, then poll:
manage_packages(action="status", job_id="<job_id>")
```

**Example — Search for a package:**
```python
manage_packages(action="search_packages", query="input system")
```

**Example — Install a package:**
```python
manage_packages(action="add_package", package="com.unity.inputsystem")
# Poll until complete:
manage_packages(action="status", job_id="<job_id>")
```

**Example — Remove with dependency check:**
```python
manage_packages(action="remove_package", package="com.unity.modules.ui")
# Error: "Cannot remove: 3 package(s) depend on it: ..."
manage_packages(action="remove_package", package="com.unity.modules.ui", force=True)
# Proceeds anyway
```

**Example — Add OpenUPM registry:**
```python
manage_packages(
    action="add_registry",
    name="OpenUPM",
    url="https://package.openupm.com",
    scopes=["com.cysharp", "com.neuecc"]
)
```

---

## Docs Tools

Tools for verifying Unity C# APIs and fetching official documentation. Group: `docs`.

### `unity_reflect`

Inspect Unity's live C# API via reflection when the target Editor's loaded API needs verification. Match signatures to the relevant Unity/package version; existing source or version-matched documentation can support offline work without opening Unity.

Requires Unity connection.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | `search`, `get_type`, or `get_member` |
| `class_name` | string | For get_type, get_member | Fully qualified or simple C# class name |
| `member_name` | string | For get_member | Method, property, or field name to inspect |
| `query` | string | For search | Search query for type name search |
| `scope` | string | No | Assembly scope for search: `unity`, `packages`, `project`, `all` (default: `unity`) |

**Actions:**

- **`search`**: Search for types by name across loaded assemblies. Returns matching type names.
- **`get_type`**: Get a member summary (names only) for a class. Returns list of methods, properties, fields.
- **`get_member`**: Get full signature detail for one member. Returns parameter types, return type, overloads.

```python
# Search for types matching a name
unity_reflect(action="search", query="NavMesh")
unity_reflect(action="search", query="Camera", scope="all")

# Get all members of a type
unity_reflect(action="get_type", class_name="UnityEngine.AI.NavMeshAgent")

# Get detailed signature for a specific member
unity_reflect(action="get_member", class_name="Physics", member_name="Raycast")
unity_reflect(action="get_member", class_name="NavMeshAgent", member_name="SetDestination")
```

### `unity_docs`

Fetch official Unity documentation from docs.unity3d.com. Returns descriptions, parameter details, code examples, and caveats. Use `unity_reflect` as an additional check when the loaded Editor API matters and a live operation is appropriate.

No Unity connection needed for doc fetching. The `lookup` action with asset-related queries will also search project assets (requires Unity connection).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | `get_doc`, `get_manual`, `get_package_doc`, or `lookup` |
| `class_name` | string | For get_doc | Unity class name (e.g., `Physics`, `Transform`) |
| `member_name` | string | No | Method or property name for get_doc |
| `version` | string | No | Unity version (e.g., `6000.0.38f1`). Auto-extracts major.minor. |
| `slug` | string | For get_manual | Manual page slug (e.g., `execution-order`) |
| `package` | string | For get_package_doc, optional for lookup | Package name (e.g., `com.unity.render-pipelines.universal`) |
| `page` | string | For get_package_doc | Package doc page (e.g., `index`, `2d-index`) |
| `pkg_version` | string | For get_package_doc, optional for lookup | Package version major.minor (e.g., `17.0`) |
| `query` | string | For lookup (single) | Single search query |
| `queries` | string | For lookup (batch) | Comma-separated queries (e.g., `Physics.Raycast,NavMeshAgent,Light2D`) |

**Actions:**

- **`get_doc`**: Fetch ScriptReference docs for a class or member. Parses HTML to extract description, signatures, parameters, return type, and code examples.
- **`get_manual`**: Fetch a Unity Manual page by slug. Returns title, sections, and code examples.
- **`get_package_doc`**: Fetch package documentation. Requires package name, page slug, and package version.
- **`lookup`**: Search doc sources in parallel (ScriptReference + Manual; also package docs if `package` + `pkg_version` provided). Supports batch queries. For asset-related queries (shader, material, texture, etc.), also searches project assets via `manage_asset`.

```python
# Fetch ScriptReference for a class
unity_docs(action="get_doc", class_name="Physics")
unity_docs(action="get_doc", class_name="Physics", member_name="Raycast")
unity_docs(action="get_doc", class_name="Transform", version="6000.0.38f1")

# Fetch a Manual page
unity_docs(action="get_manual", slug="execution-order")
unity_docs(action="get_manual", slug="urp/urp-introduction")

# Fetch package documentation
unity_docs(action="get_package_doc", package="com.unity.render-pipelines.universal",
           page="2d-index", pkg_version="17.0")

# Parallel lookup across all sources (single query)
unity_docs(action="lookup", query="Physics.Raycast")

# Batch lookup (multiple queries in one call)
unity_docs(action="lookup", queries="Physics.Raycast,NavMeshAgent,Light2D")

# Lookup with package docs included
unity_docs(action="lookup", query="VolumeProfile",
           package="com.unity.render-pipelines.universal", pkg_version="17.0")
```
