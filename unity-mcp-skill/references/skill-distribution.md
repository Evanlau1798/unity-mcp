# Customized skill distribution

Read this procedure only when maintaining, installing or synchronizing the skill.

## Source and layout

The maintained fork is [Evanlau1798/unity-mcp](https://github.com/Evanlau1798/unity-mcp), with the customized distribution on `main`. Keep `unity-mcp-skill/` and `.claude/skills/unity-mcp-skill/` identical for their distributed files. The existing Unity skill synchronizer reads the latter fixed subtree even when the destination client is Codex.

Treat the fork branch, resolved commit, local source tree and installed skill as distinct identities. A local edit or commit is not evidence that the remote branch contains it. Check the actual remote subtree before synchronization.

## Select the actual source

The repository selected by the synchronizer is independent of Git remotes and of the Unity package URL. Changing Git's `origin` or `upstream` does not configure skill synchronization.

In implementations with `SkillSyncService.SyncAsync(repoUrl, installDir, branch, ...)`, select the explicit fork URL and `main`. The overload without `repoUrl` uses its compiled default. Check the caller: the main window's Install Skills action may still use that default even when a separate installer stores `UnityMcpSkillSync.RepoUrl`. Do not infer that all entrypoints share that preference, or claim a menu exists without observing it.

The current service resolves a branch through the GitHub branches API and uses the resulting commit for all files in that one sync. This is a consistent per-operation snapshot, not a persistent SHA/tag pin; the next sync resolves the branch again. Do not put a commit SHA into a branch-only field.

## Preserve custom content

The existing service mirrors the remote subtree: changed files are overwritten and local files absent remotely are deleted. Its internal plan is applied immediately; a log of that plan is not an approval preview or a local-customization merge. An ownership marker identifies the managed root, not a protected custom version. A partial apply is possible if downloading or writing fails.

Before an authorized sync:
- Confirm all intended custom references are present in the selected remote subtree, and retain a recoverable version outside the managed install root.
- Scope the destination to this skill's directory, never the parent directory containing all skills. Keep unrelated sibling skills outside that root.
- If source identity or local differences are uncertain, inspect the selected snapshot in an isolated staging directory before replacing the installed copy. Do not invoke a mirroring operation on custom files merely to discover what it would delete.
- Verify the installed file set/content against the selected source after completion. Preserve the original error and partial state if verification fails.

Routine Unity use and MCP package/server updates do not require a skill sync. Keep synchronization and any runtime changes within the user's authorized scope.
