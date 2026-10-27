---
name: unity-mcp-orchestrator
description: Operate Unity Editor through MCP and choose focused validation for scene, asset, script, test, compile, reload and recovery tasks. Use for Unity MCP automation or uncertain or slow test jobs; documentation-only work needs no live Editor.
---

# Unity MCP Orchestrator

Use the installed MCP schema and the exact Unity project/Editor as the authority for available operations. Preserve project acceptance criteria, user assets and original failure evidence.

## Choose the task and read only what it needs

- For offline design, source inspection, documentation or skill maintenance, work from files and existing evidence. Do not open Unity, resume a Goal, refresh, build or run tests merely to read this skill.
- For live operations, resolve the target and relevant state/resources before mutation. Reuse unchanged guidance; inspect only the resources and references needed for the current decision.
- For VRChat Avatar behavior, native mechanism choices, MA/NDMF and SDK/client/multi-client evidence, use the installed **Vrchat Avatar Props** (`vrchat-avatar-props`) skill when applicable. General Unity work does not require that skill.
- Keep project IDs, paths, counts and temporary progress in appropriate existing project records. This skill does not require a new plan, research report or handoff file.

| Need | Read |
| --- | --- |
| Tool actions, parameters and examples | [Tool index](references/tools-reference.md); follow only the relevant category |
| Resource URIs, response shapes and pagination | [Resources](references/resources-reference.md) |
| Scene/assets, scripts, UI/input, camera/modeling, graphics, packages/API or batch examples | [Workflow index](references/workflows.md); select the relevant topic |
| Validation scope, one mutation owner and completion | [Validation procedure](references/validation-workflow.md) |
| Candidate identity, test selection, valid premises, evidence reuse or timing | [Evidence and selection](references/evidence-and-selection.md) |
| Timeout, disconnect, reload, native ownership, cleanup or runner-contract changes | [Lifecycle and recovery](references/lifecycle-and-recovery.md) |
| ProBuilder modeling details | [ProBuilder guide](references/probuilder-guide.md) |
| Maintaining or synchronizing this customized skill | [Distribution contract](references/skill-distribution.md) |

Examples are templates, not proof of API availability or project compatibility. Inspect actual targets/components and adapt names, enums, paths and serialized payloads. Validate the changed boundary with relevant console output, resources, tests or images; do not run unrelated gates.

## Resolve tools and target

1. Inspect advertised tools and the installed schema. A hidden tool may belong to an inactive group: inspect `manage_tools(action="list_groups")` and activate only the needed group. HTTP and stdio visibility can differ.
2. Project-scoped custom tools may appear only in `mcpforunity://custom-tools`; invoke them through `execute_custom_tool` when that registration mode is enabled. Do not treat a missing direct tool name as a broken connection.
3. For live work, identify the exact project/instance through available resources. Where supported, explicit per-call targeting takes precedence over an instance-bound endpoint, then legacy session/automatic selection. A bound-endpoint conflict is an error; do not silently switch projects.
4. Re-resolve targets after reload or a new Editor instance. Preserve relevant scene/asset dirty ownership and existing jobs.

## Keep mutations and observations separate

- Use one mutation owner per Editor/AssetDatabase/job. Delegate only independent offline work with explicit file scopes. Every subagent must start Fresh, without Fork or inherited coordinator history; follow the applicable global review discipline.
- Read the actual response envelope, including nested data/result/error and text-encoded JSON where exposed. Transport success alone does not establish native success.
- After a script tool already imports/compiles, observe that operation and its completion evidence; do not launch a redundant refresh. For external edits, request only the refresh required by the installed contract.
- Interpret v2 Editor state through `data.compilation`, `data.assets.refresh`, `data.tests`, `data.advice` and `data.staleness`. A stale snapshot or `is_compiling=false` alone does not prove a particular operation completed.
- Preserve operation/job identity across timeout or reconnect. Re-query the accepted work; do not replay a mutation, force-reset a tracker or change transport merely because observation failed. Use [lifecycle and recovery](references/lifecycle-and-recovery.md) for uncertain states.
- Verify native cleanup, ownership release and required restoration before the next mutation. Missing fields are unknown, not false.

## Select and observe tests

Use [validation](references/validation-workflow.md) before planning or starting a test run, and the testing category in the [tool index](references/tools-reference.md) for exact parameters.

- Discover first. If advertised, `list_tests` is read-only and supports mode/name filtering and pagination; follow its actual `data.nextCursor`/`data.hasMore`. Do not use `run_tests` to enumerate tests. An older resource may expose only the first page; report that limit instead of assuming completeness.
- Start the selected run once. In the current fork, the decoded start response contains `data.job_id`; observations use `data.status`, with `succeeded`, `failed` and `cancelled` terminal states, and results under `data.result`.
- Preserve original failed/skipped/unknown outcomes and reconcile requested cases with actual leaves/profiles. Follow result/output pagination when needed; polling timeout does not cancel the run.
- Terminal status still requires cleanup/ownership confirmation. A new reviewer or commit alone does not invalidate matching evidence; rerun for a changed input, an unresolved risk or an explicit gate.

## Batch compatible native commands

Use `batch_execute` to reduce RPC round trips when its native commands and payloads match the task. It is not a general executor for every Python MCP wrapper: child commands do not inherit wrapper encoding, preflight or recovery.

The current native implementation executes sequentially on Unity's main thread; `parallel` is a hint and `parallelApplied=false`. The configured limit defaults to 25, ranges from 1 to 100 and has a hard maximum of 100. `fail_fast` defaults to false; set it deliberately for dependent commands. Batches are not transactions and have no rollback: inspect per-command results and any partial changes before continuing.

Preserve nested property keys; only documented top-level normalization applies. Batch RPC does not imply AssetDatabase import batching, and does not justify disabling reload or loosening product acceptance.
