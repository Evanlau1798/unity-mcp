from __future__ import annotations

import json
import os
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable


_SCOPES = ("assets", "scripts")
_SCRIPT_SUFFIXES = {".cs", ".js", ".boo", ".asmdef", ".asmref", ".rsp", ".dll"}
_SKIP_DIRS = {"library", "temp", "logs", "obj", ".git", "node_modules"}


def _now_unix_ms() -> int:
    return int(time.time() * 1000)


def _in_pytest() -> bool:
    return bool(os.environ.get("PYTEST_CURRENT_TEST"))


@dataclass(frozen=True)
class ExternalChangesSnapshot:
    """The exact observed generations covered by one refresh request."""

    instance_id: str
    epoch: str
    project_root: str | None
    scope: str
    generations: tuple[tuple[str, int], ...]
    reliable: bool
    dirty: bool


@dataclass
class ExternalChangesState:
    project_root: str | None = None
    last_scan_unix_ms: int | None = None
    last_seen_mtime_ns: int | None = None
    dirty: bool = True
    dirty_since_unix_ms: int | None = None
    external_changes_last_seen_unix_ms: int | None = None
    last_cleared_unix_ms: int | None = None
    extra_roots: list[str] | None = None
    manifest_last_mtime_ns: int | None = None
    epoch: str = field(default_factory=lambda: uuid.uuid4().hex)
    generation: int = 0
    observed: dict[str, int] = field(default_factory=lambda: dict.fromkeys(_SCOPES, 0))
    acknowledged: dict[str, int] = field(default_factory=lambda: dict.fromkeys(_SCOPES, 0))
    fingerprints: dict[str, tuple[str, int, int, int]] | None = None
    scan_complete: bool = False
    scan_entry_budget: int = 0


class ExternalChangesScanner:
    """Stat-based observation and scoped acknowledgement of external changes.

    Ordinary state reads are throttled and bounded. Explicit capture/ACK scans
    are complete, so a project larger than the ordinary scan limit can still
    synchronize. A partial scan never replaces the last complete baseline.
    """

    def __init__(self, *, scan_interval_ms: int = 1500, max_entries: int = 20000):
        self._states: dict[str, ExternalChangesState] = {}
        self._scan_interval_ms = int(scan_interval_ms)
        self._max_entries = int(max_entries)

    def _get_state(self, instance_id: str) -> ExternalChangesState:
        return self._states.setdefault(instance_id, ExternalChangesState())

    def set_project_root(self, instance_id: str, project_root: str | None) -> None:
        if not project_root:
            return
        canonical = os.path.normcase(str(Path(project_root).resolve()))
        st = self._get_state(instance_id)
        if st.project_root != canonical:
            # A reused instance id must not acknowledge another project's work.
            self._states[instance_id] = ExternalChangesState(project_root=canonical)

    @staticmethod
    def _scope_names(scope: str) -> tuple[str, ...]:
        if scope == "all":
            return _SCOPES
        if scope in _SCOPES:
            return (scope,)
        raise ValueError(f"Unknown refresh scope: {scope}")

    def _snapshot(self, instance_id: str, scope: str) -> ExternalChangesSnapshot:
        st = self._get_state(instance_id)
        scopes = self._scope_names(scope)
        reliable = bool(st.project_root and st.scan_complete and st.fingerprints is not None)
        return ExternalChangesSnapshot(
            instance_id=instance_id,
            epoch=st.epoch,
            project_root=st.project_root,
            scope=scope,
            generations=tuple((name, st.observed[name]) for name in scopes),
            reliable=reliable,
            dirty=not reliable or any(st.observed[name] > st.acknowledged[name] for name in scopes),
        )

    def capture(self, instance_id: str, scope: str = "all") -> ExternalChangesSnapshot:
        self._scope_names(scope)
        self.update_and_get(instance_id, force=True)
        return self._snapshot(instance_id, scope)

    def acknowledge(self, instance_id: str, snapshot: ExternalChangesSnapshot) -> bool:
        st = self._get_state(instance_id)
        if (not isinstance(snapshot, ExternalChangesSnapshot) or not snapshot.reliable
                or snapshot.instance_id != instance_id or snapshot.epoch != st.epoch
                or snapshot.project_root != st.project_root):
            return False
        try:
            scopes = self._scope_names(snapshot.scope)
        except ValueError:
            return False
        if tuple(name for name, _ in snapshot.generations) != scopes:
            return False
        self.update_and_get(instance_id, force=True)
        if not st.scan_complete:
            return False
        if any(not isinstance(value, int) or value < 0 or value > st.observed[name]
               for name, value in snapshot.generations):
            return False
        for name, value in snapshot.generations:
            st.acknowledged[name] = max(st.acknowledged[name], value)
        st.last_cleared_unix_ms = _now_unix_ms()
        self._update_dirty(st)
        return True

    def clear_dirty(self, instance_id: str) -> bool:
        """Compatibility only: ACK the last observation, never reset its baseline.

        New refresh callers must retain capture()'s token across the operation.
        The forced observation inside acknowledge() preserves intervening edits.
        """
        return self.acknowledge(instance_id, self._snapshot(instance_id, "all"))

    @staticmethod
    def _file_scope(path: Path) -> str:
        source = path.with_suffix("") if path.suffix.lower() == ".meta" else path
        if (any(part.lower() == "projectsettings" for part in source.parts)
                or source.name.lower() in {"manifest.json", "packages-lock.json", "package.json"}):
            return "all"
        return "scripts" if source.suffix.lower() in _SCRIPT_SUFFIXES else "assets"

    def _scan_paths(self, roots: Iterable[Path], *, entry_budget: int | None):
        fingerprints: dict[str, tuple[str, int, int, int]] = {}
        complete = True
        entries = 0

        def failed(_error):
            nonlocal complete
            complete = False

        for root in roots:
            try:
                root.stat()
            except FileNotFoundError:
                continue
            except OSError:
                complete = False
                continue
            for dirpath, dirnames, filenames in os.walk(str(root), onerror=failed):
                entries += 1
                if entry_budget is not None and entries > entry_budget:
                    return fingerprints, False, entries
                dp = Path(dirpath)
                if dp.name.lower() in _SKIP_DIRS:
                    dirnames[:] = []
                    continue
                dirnames[:] = [d for d in dirnames
                               if not d.startswith(".") and d.lower() not in _SKIP_DIRS]
                for name in filenames:
                    if name.startswith("."):
                        continue
                    entries += 1
                    if entry_budget is not None and entries > entry_budget:
                        return fingerprints, False, entries
                    path = dp / name
                    try:
                        stat = path.stat()
                    except OSError:
                        complete = False
                        continue
                    mtime = getattr(stat, "st_mtime_ns", int(stat.st_mtime * 1_000_000_000))
                    ctime = getattr(stat, "st_ctime_ns", int(stat.st_ctime * 1_000_000_000))
                    fingerprints[os.path.normcase(str(path))] = (
                        self._file_scope(path), int(mtime), int(stat.st_size), int(ctime),
                    )
        return fingerprints, complete, entries

    def _resolve_manifest_extra_roots(self, project_root: Path, st: ExternalChangesState) -> list[Path]:
        manifest_path = project_root / "Packages" / "manifest.json"
        try:
            stat = manifest_path.stat()
        except FileNotFoundError:
            st.extra_roots = []
            st.manifest_last_mtime_ns = None
            return []
        # Read this small file on each scan, including restored/backdated manifests.
        doc = json.loads(manifest_path.read_text(encoding="utf-8"))
        deps = doc.get("dependencies") if isinstance(doc, dict) else None
        roots: list[str] = []
        if isinstance(deps, dict):
            for version in deps.values():
                if not isinstance(version, str) or not version.strip().startswith("file:"):
                    continue
                suffix = version.strip()[len("file:"):].strip()
                if suffix.startswith("///"):
                    # A file URI may contain a Windows drive or a POSIX absolute path.
                    suffix = suffix[3:]
                    candidate = Path(suffix if len(suffix) > 1 and suffix[1] == ":" else "/" + suffix)
                elif Path(suffix).is_absolute():
                    candidate = Path(suffix)
                else:
                    candidate = (manifest_path.parent / suffix).resolve()
                if candidate.is_dir():
                    roots.append(str(candidate))
        st.extra_roots = list(dict.fromkeys(roots))
        st.manifest_last_mtime_ns = getattr(stat, "st_mtime_ns", int(stat.st_mtime * 1_000_000_000))
        return [Path(path) for path in st.extra_roots]

    @staticmethod
    def _update_dirty(st: ExternalChangesState) -> None:
        st.dirty = not st.scan_complete or any(
            st.observed[name] > st.acknowledged[name] for name in _SCOPES
        )
        if not st.dirty:
            st.dirty_since_unix_ms = None
        elif st.dirty_since_unix_ms is None:
            st.dirty_since_unix_ms = _now_unix_ms()

    @staticmethod
    def _payload(st: ExternalChangesState) -> dict:
        return {
            "external_changes_dirty": st.dirty,
            "external_changes_last_seen_unix_ms": st.external_changes_last_seen_unix_ms,
            "dirty_since_unix_ms": st.dirty_since_unix_ms,
            "last_cleared_unix_ms": st.last_cleared_unix_ms,
            "external_changes_scan_complete": st.scan_complete,
            "external_changes_generation": st.generation,
            "external_changes_generations": dict(st.observed),
            "external_changes_acknowledged_generations": dict(st.acknowledged),
        }

    def update_and_get(self, instance_id: str, *, force: bool = False) -> dict:
        st = self._get_state(instance_id)
        if _in_pytest() and not force:
            return self._payload(st)
        now = _now_unix_ms()
        if (not force and st.last_scan_unix_ms is not None
                and now - st.last_scan_unix_ms < self._scan_interval_ms):
            return self._payload(st)
        st.last_scan_unix_ms = now
        if not st.project_root:
            st.scan_complete = False
            self._update_dirty(st)
            return self._payload(st)

        root = Path(st.project_root)
        if not root.is_dir():
            st.scan_complete = False
            self._update_dirty(st)
            return self._payload(st)
        roots = [root / "Assets", root / "ProjectSettings", root / "Packages"]
        manifest_complete = True
        try:
            roots.extend(self._resolve_manifest_extra_roots(root, st))
        except (OSError, ValueError):
            manifest_complete = False
        budget = None if force else max(self._max_entries, st.scan_entry_budget)
        current, complete, entries = self._scan_paths(roots, entry_budget=budget)
        st.scan_complete = complete and manifest_complete
        if st.scan_complete:
            # Once a large project was fully observed, ordinary polls must also
            # be able to finish. Keep a bounded margin for routine file growth.
            st.scan_entry_budget = max(st.scan_entry_budget, entries + max(128, entries // 4))
            previous = st.fingerprints
            if previous is None:
                # Observation alone cannot prove Unity loaded this initial snapshot.
                changed_scopes = set(_SCOPES)
            else:
                changed_paths = {path for path in previous.keys() | current.keys()
                                 if previous.get(path) != current.get(path)}
                changed_kinds = {(current.get(path) or previous[path])[0] for path in changed_paths}
                changed_scopes = set(_SCOPES) if "all" in changed_kinds else changed_kinds
            if changed_scopes:
                st.generation += 1
                for name in changed_scopes:
                    st.observed[name] = st.generation
                st.external_changes_last_seen_unix_ms = now
            st.fingerprints = current
            st.last_seen_mtime_ns = max((item[1] for item in current.values()), default=None)
        self._update_dirty(st)
        return self._payload(st)


external_changes_scanner = ExternalChangesScanner()
