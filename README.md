<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-header-dark.png">
    <img alt="MCP for Unity" src="docs/images/logo-header-light.png" width="400">
  </picture>
</p>

<div align="center">

[English](README.md) <img src="docs/images/connector.svg" alt="↔" height="14"> [简体中文](docs/i18n/README-zh.md) &nbsp;&nbsp;&nbsp;|&nbsp;&nbsp;&nbsp; [Discord](https://discord.gg/y4p8KfzrN4) <img src="docs/images/connector.svg" alt="↔" height="14"> [Wiki](https://coplaydev.github.io/unity-mcp/)

#### Proudly sponsored and maintained by [Aura](https://www.tryaura.dev/) — the AI assistant for Unreal & Unity.
##### And don't miss [Godot AI](https://github.com/hi-godot/godot-ai), the new open source project from the makers of MCP for Unity.

</div>

<p align="center"><b>Create your Unity apps with LLMs.</b> MCP for Unity bridges AI assistants — Claude, Codex, VS Code, local LLMs, and more — with your Unity Editor via <a href="https://modelcontextprotocol.io/introduction">Model Context Protocol</a>. Give your LLM the tools to manage assets, control scenes, edit scripts, run tests, and automate your game dev workflows.</p>

<p align="center">
  <img alt="MCP for Unity building a scene" src="docs/images/building_scene.gif">
</p>

---

<!-- recent-updates:start -->
<details>
<summary><strong>Recent Updates</strong></summary>

* **[v10.3.0](https://github.com/CoplayDev/unity-mcp/releases/tag/v10.3.0)** (2026-10-04)
* **[v10.2.0](https://github.com/CoplayDev/unity-mcp/releases/tag/v10.2.0)** (2026-09-01)
* **[v10.1.2](https://github.com/CoplayDev/unity-mcp/releases/tag/v10.1.2)** (2026-08-02)
* **[v10.1.0](https://github.com/CoplayDev/unity-mcp/releases/tag/v10.1.0)** (2026-07-13)
* **[v10.0.2](https://github.com/CoplayDev/unity-mcp/releases/tag/v10.0.2)** (2026-07-13)

Full history: [Release Notes](https://coplaydev.github.io/unity-mcp/releases).

</details>
<!-- recent-updates:end -->

---

## What it does

Control the Unity Editor in natural language from any MCP client — create scenes & GameObjects, edit C# scripts, manage assets, run tests, profile, and build. 50 focused MCP tool entrypoints, any client, free & MIT.

**[Browse the full tool catalog →](https://coplaydev.github.io/unity-mcp/reference/tools/)**

---

## Quickstart

**Requirements:** Unity **2021.3 LTS or newer**, Git on your PATH, Python
**3.10+** with [`uv`](https://docs.astral.sh/uv/), and an MCP client that supports
HTTP, such as Codex. Use the same fork commit for the Unity package and Python
server. The steps below pin both to [`213a9fc3`](https://github.com/Evanlau1798/unity-mcp/commit/213a9fc3e93dd94414f1aba6c5d3c1f728dbcdb2).

1. **Install the Unity package.** Open **Window → Package Manager → + → Add
   package from git URL…** and paste:

   ```text
   https://github.com/Evanlau1798/unity-mcp.git?path=/MCPForUnity#213a9fc3e93dd94414f1aba6c5d3c1f728dbcdb2
   ```

2. **Set the Python server source.** Clone the fork outside your Unity project
   and check out the same commit:

   ```bash
   git clone --branch main --single-branch https://github.com/Evanlau1798/unity-mcp.git
   git -C unity-mcp checkout --detach 213a9fc3e93dd94414f1aba6c5d3c1f728dbcdb2
   ```

   In **Window → MCP for Unity → Advanced**, set **Server Source** to the full
   path of that checkout's `Server` folder. If you're upgrading an existing
   Unity-managed server, wait until the other Editors sharing it have closed,
   then use **Stop Server** followed by **Start Server** in the managing Editor
   to apply the new source.

3. **Connect over local HTTP.** In the **Connect** tab, select **HTTP Local**
   and click **Start Server**. If the matching fork server is already running
   for another Editor, click **Connect** to join it. Configure your client from
   the same tab or set its HTTP MCP URL to `http://127.0.0.1:8080/mcp` (adjust
   the port if you changed the server's **HTTP URL**).

4. **Verify the target Editor.** Ask your client to read
   `mcpforunity://editor/state` and report the project path and Play Mode state.
   If several Editors share the server, first read `mcpforunity://instances` and
   use `set_active_instance` with the intended Editor's exact `Name@hash`. To
   keep that target across new MCP sessions, use the client URL
   `http://127.0.0.1:8080/mcp/instance/Name@hash` with your actual instance ID.

---

## Performance and reliability — Evanlau1798 fork

This fork improves refresh/test coordination, recovery across domain reload, and
observability. **Pin the Unity package and Python server to the same commit SHA**;
the upstream release version strings alone do not identify these changes.

### Optimized workflow

- **Generation-aware preflight:** assets-only changes request only an asset
  import; scripts request compilation; mixed changes or incomplete scanner data
  retain the conservative full refresh. A matching native completion receipt
  acknowledges only the captured dirty generations.
- **Safe test admission:** `external_changes_pending`, `refresh_pending`, or
  retained native cleanup ownership block the next test. Wait for the original
  refresh to complete rather than launching the same native job repeatedly.
  When an ACKed refresh leaves only a new, reliably observed assets generation,
  test preflight performs **at most one** assets-only catch-up without compilation;
  it never retries a new script generation, stale state, or failed ACK blindly.
- **Compact job results:** terminal `get_test_job` replies include a maximum
  of three result leaves and 1,024 characters per message, stack trace and
  output field. Use `result_offset` and `detail_offset` on the **same job ID**
  to retrieve the remaining original native content. The returned
  `result_page`/`detail_page` metadata describes the available pages;
  the native job and its original result are never changed. Progress failure
  messages use 160-character windows with `message_next_offset` so the
  same `detail_offset` can recover their original text even before terminal.
- **Recoverable job polling:** `get_test_job` retries transient
  `no_unity_session` and `stale_connection` responses within the same
  `wait_timeout`, preserving the job ID and last confirmed status.
- **WebSocket send failure cleanup:** closes unawaited command Futures without
  hiding the real transport error or replaying an uncertain mutation.
- **Staleness-aware state:** if supplemental file scanning alone expires an
  initially fresh native editor snapshot, request at most one bounded,
  read-only reobservation. Unavailable or mismatched state remains stale.
- **Unambiguous timing:** `get_test_job.data.job_wall_duration_ms` spans
  native start to terminal, and `observed_at_unix_ms` timestamps the
  observation. NUnit `result.summary.durationSeconds` is retained separately.

### Measured earlier lifecycle-repair baseline

CometTether, Windows / Unity 2022.3.22f1 / UTF 1.1.33:

| Tool-call latency, median | Older CP02J sample | Lifecycle repair (12 h) |
| --- | ---: | ---: |
| `refresh_unity` | 73.6 s (n=12) | 2.58 s (n=139) |
| `run_tests` admission | 72.0 s (n=10) | 0.66 s (n=108) |
| `get_test_job` | 23.4 s (n=24) | 0.41 s (n=127) |

The repaired 12-hour sample also contained 36 `external_changes_pending`
admissions and 18 `no_unity_session` observations, which motivated this
follow-up. **These are different workloads and observation windows, not a
controlled benchmark of this newer patch or an overall development speedup.**
A long job poll may be an intentional wait for a native test.

For safe operation, edit coherent batches, read the exact refresh operation
receipt and generation ACK, then start each test once and poll the *same* job ID
until the terminal result and native cleanup release. A network timeout or
outer `isError: false` does not establish underlying Unity success.

## Community

- [Discord](https://discord.gg/y4p8KfzrN4) — chat with maintainers and other contributors
- [Issues](https://github.com/CoplayDev/unity-mcp/issues) — bugs and feature requests
- [Discussions](https://github.com/CoplayDev/unity-mcp/discussions) — design ideas and broader questions
- Security: see [SECURITY.md](SECURITY.md) for private reporting

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Branch off `beta`, not `main`. The full dev setup, testing, and release process live in the [Contributing](https://coplaydev.github.io/unity-mcp/contributing/dev-setup) docs.

## Advanced

- **Multiple Unity instances** — [Multi-Instance Routing](https://coplaydev.github.io/unity-mcp/guides/multi-instance)
- **Tool groups (vfx / animation / ui / testing / etc.)** — [Tool Groups](https://coplaydev.github.io/unity-mcp/guides/tool-groups)
- **v10 asset generation and upgrade notes** — [v10 Migration](https://coplaydev.github.io/unity-mcp/migrations/v10)
- **Roslyn script validation** — [Roslyn Validation](https://coplaydev.github.io/unity-mcp/guides/roslyn)
- **Remote-hosted server with auth** — [Remote Server Auth](https://coplaydev.github.io/unity-mcp/guides/remote-server-auth)

## Star History

[![Star History Chart](https://star-history.dera.page/svg?repos=CoplayDev/unity-mcp&type=Date)](https://star-history.dera.page/#CoplayDev/unity-mcp&Date)

## Citation

If MCP for Unity helped your research, please cite it.

```bibtex
@inproceedings{wu2025mcpunity,
  author    = {Wu, Shutong and Barnett, Justin P.},
  title     = {{MCP-Unity}: {Protocol-Driven} Framework for Interactive {3D} Authoring},
  year      = {2025},
  isbn      = {9798400721366},
  publisher = {Association for Computing Machinery},
  address   = {New York, NY, USA},
  url       = {https://doi.org/10.1145/3757376.3771417},
  doi       = {10.1145/3757376.3771417},
  series    = {SA Technical Communications '25}
}
```

## Unity AI Tools by Aura

Aura offers 2 AI tools for Unity:
- **MCP for Unity** is available freely under the MIT license.
- **Aura for Unity** is a premium Unity/Unreal AI assistant built for game devs.

## Disclaimer

This project is a free and open-source tool for the Unity Editor, and is not affiliated with Unity Technologies.

---

**License:** MIT — see [LICENSE](LICENSE).
