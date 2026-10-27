# Packages, deployment, and API verification

[Workflow index](workflows.md). Read only the package, deployment, or API procedure needed by the task.

> **Template:** Package names, versions, URLs, and API examples require the current project's context. Verify the template against the installed schema and project before use. Package jobs use their own installed status contract; do not assume test-job status fields apply to them.

Use [setup/readiness](workflow-setup.md) before Editor mutations. Package installation/removal, registry edits, and deployment belong to the requested task scope; viewing this reference does not initiate them. Inspect existing dependencies/configuration first and retain the actual job or deployment receipt.

## Package Management Workflows

### Install a Package and Verify

```python
manage_packages(action="ping")
listing = manage_packages(action="list_packages")
# If listing is async, copy its actual returned job_id and observe that job:
manage_packages(action="status", job_id=listing_job_id)
```

Read the installed package/version results before choosing the requested package. After an intended installation, inspect acceptance and observe the returned job through package status:

```python
installation = manage_packages(action="add_package", package="com.unity.inputsystem")
# installation_job_id is the handle actually returned by this request.
manage_packages(action="status", job_id=installation_job_id)
```

Continue bounded observation of the same job until its actual terminal state. A domain reload or response timeout does not justify reinstalling. Confirm current import/compile readiness, read relevant Console errors, and verify the installed package:

```python
read_console(types=["error"], count=10)
manage_packages(action="get_package_info", package="com.unity.inputsystem")
```

Installation does not by itself change project input handling. Verify or adjust only the settings required by the requested [input-system workflow](workflow-input.md).

### Add OpenUPM Registry and Install Package

Inspect the existing scoped registries first; preserve unrelated registries and scopes. This example applies when the task requires the specified package and registry:

```python
manage_packages(
    action="add_registry", name="OpenUPM",
    url="https://package.openupm.com", scopes=["com.cysharp"]
)
# Inspect the registry-write result before resolution.
resolution = manage_packages(action="resolve_packages")
# Observe any returned resolution job and complete import/reload before adding.
installation = manage_packages(action="add_package", package="com.cysharp.unitask")
manage_packages(action="status", job_id=installation_job_id)
```

Use the actual handle from each asynchronous response; do not reuse a listing or resolution ID for the installation. Verify the installed package/version and Console after completion.

### Safe Package Removal

Inspect package metadata and actual dependents before issuing a removal; a removal request is not a read-only dependency probe.

```python
manage_packages(action="get_package_info", package="com.example.package")
# Inspect the installed dependency graph and intended removal impact first.
removal = manage_packages(action="remove_package", package="com.example.package")
manage_packages(action="status", job_id=removal_job_id)
```

If the tool reports dependents, resolve that dependency decision rather than automatically retrying. `force=True` is available when the requested change deliberately includes the affected dependency impact:

```python
# Conditional alternative after the dependency decision; not an automatic retry.
manage_packages(action="remove_package", package="com.example.package", force=True)
```

Observe the actual removal job, then verify remaining dependencies, import/compile state, and relevant Console errors.

### Install from Git URL (e.g., NuGetForUnity)

Choose the requested repository/ref and confirm it matches the intended dependency. The unpinned URL below is an example; use the project's pinned revision when reproducibility requires it.

```python
installation = manage_packages(
    action="add_package",
    package="https://github.com/GlitchEnzo/NuGetForUnity.git?path=/src/NuGetForUnity"
)
manage_packages(action="status", job_id=installation_job_id)
```

Finish the same package-status, compilation/Console, and installed-version verification procedure. Keep the source/ref in the existing dependency evidence.

## Package Deployment Workflows

### Iterative Development Loop (Edit → Deploy → Test)

Use `manage_editor(action="deploy_package")` only for the intended MCPForUnity package deployment. Inspect the configured source and destination and the backup behavior first; the source path must already be configured in MCP for Unity Advanced Settings. Editing skill documentation alone does not authorize or require deployment.

1. Make the intended code changes in the package's source repository through its normal file-editing workflow. Assets-only script tools are not general editors for package repository files.
2. Deploy once and inspect the actual copy/backup receipt.
3. Observe the import/recompilation that deployment initiates, using fresh v2 state and actual receipts. Do not issue an extra force refresh merely to wait.
4. Check current Console errors and confirm the installed package bytes/version match the intended candidate. Python server and UPM package must correspond when the change spans both.
5. Run only the affected tests or required project gate, using the complete [test job/cleanup procedure](workflow-testing.md).

```python
deployment = manage_editor(action="deploy_package")
# Inspect deployment, observe its import/reload, and verify the installed candidate.
read_console(types=["error"], count=10, include_stacktrace=True)

# After readiness and candidate verification; names come from current discovery.
started = run_tests(mode="EditMode", test_names=affected_test_names)
if started.get("success") is not True:
    raise RuntimeError(started)
job_id = started["data"]["job_id"]
observed = get_test_job(job_id=job_id, wait_timeout=30)
# Follow workflow-testing.md through terminal result and cleanup/ownership release.
```

### Rollback After Failed Deploy

When restoration is the intended recovery, inspect the automatic backup and current native ownership before applying it. Do not restore while a run still owns the Editor, or overwrite unrelated changes without resolving ownership.

```python
restoration = manage_editor(action="restore_package")
# Inspect the restore result, then observe the import/reload already initiated.
# Confirm the restored package identity and read current Console errors.
```

Verify the restored candidate and required environment state. A restoration receipt alone does not prove tests passed, and a later successful run does not overwrite the original deployment/test failure. For timeout/reload, use [lifecycle and recovery](lifecycle-and-recovery.md).

## API Verification Workflows

> These tools belong to the opt-in `docs` group. If they are not enabled, inspect the current tool-group state and activate it with `manage_tools(action="activate", group="docs")`. Tool visibility is not proof that a package/API exists.

### Full API Verification Before Writing Code

Use `unity_reflect` and `unity_docs` when API availability/signatures matter to the code you are about to write. Reuse relevant evidence for the same installed version instead of rechecking unrelated APIs.

**Trust hierarchy:** reflection (live runtime) > project assets > official docs.

```python
# Step 1: Search for the type you need
unity_reflect(action="search", query="NavMesh")
# → Returns matching types: NavMeshAgent, NavMeshPath, NavMeshHit, etc.

# Step 2: Get member summary for the type
unity_reflect(action="get_type", class_name="UnityEngine.AI.NavMeshAgent")
# → Returns all methods, properties, fields (names only)

# Step 3: Get full signature for specific members you plan to use
unity_reflect(action="get_member", class_name="NavMeshAgent", member_name="SetDestination")
# → Returns parameter types, return type, all overloads

# Step 4: Get official docs for usage patterns and examples
unity_docs(action="get_doc", class_name="NavMeshAgent", member_name="SetDestination")
# → Returns description, signatures, parameters, code examples
```

### Batch API Lookup

Use `unity_docs` `lookup` action to search multiple APIs in a single call:

```python
# Search ScriptReference + Manual in parallel (+ package docs if package/pkg_version provided)
unity_docs(action="lookup", queries="Physics.Raycast,NavMeshAgent,Light2D")

# Include package docs in the search
unity_docs(action="lookup", query="VolumeProfile",
           package="com.unity.render-pipelines.universal", pkg_version="17.0")
```

### Finding Shaders and Materials in Project

The `lookup` action automatically searches project assets for asset-related queries:

```python
# This searches both docs AND project assets for shader-related content
unity_docs(action="lookup", query="Lit shader")
# → Returns doc hits + matching project assets (shaders, materials, etc.)
```

### Manual and Package Documentation

```python
# Fetch Unity Manual pages (execution order, scripting concepts, etc.)
unity_docs(action="get_manual", slug="execution-order")

# Fetch package-specific documentation
unity_docs(action="get_package_doc",
           package="com.unity.render-pipelines.universal",
           page="2d-index", pkg_version="17.0")
```

### Verifying APIs Across Unity Versions

```python
# Specify Unity version for version-specific docs
unity_docs(action="get_doc", class_name="Camera", member_name="main", version=observed_unity_version)

# Use reflection to check what's actually available in the running editor
unity_reflect(action="search", query="InputAction", scope="packages")
```

Documentation describes the referenced version; live reflection establishes available signatures, not that a behavior or performance claim has been tested. Adapt package-doc versions to the installed packages and preserve any remaining evidence limits.
