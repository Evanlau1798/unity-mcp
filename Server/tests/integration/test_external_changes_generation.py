import os

import pytest

from services.state.external_changes_scanner import ExternalChangesScanner


@pytest.fixture
def project(tmp_path, monkeypatch):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)
    root = tmp_path / "Project"
    for name in ("Assets", "Packages", "ProjectSettings"):
        (root / name).mkdir(parents=True)
    return root


def scanner_for(root, **kwargs):
    scanner = ExternalChangesScanner(scan_interval_ms=0, **kwargs)
    scanner.set_project_root("Test@generation", str(root))
    return scanner


def test_deletion_of_previously_observed_file_marks_dirty(project):
    target = project / "Assets" / "deleted.cs"
    target.write_text("// baseline", encoding="utf-8")
    scanner = scanner_for(project)
    scanner.update_and_get("Test@generation")
    scanner.clear_dirty("Test@generation")
    scanner.update_and_get("Test@generation")
    target.unlink()
    assert scanner.update_and_get("Test@generation")["external_changes_dirty"] is True


def test_backdated_change_below_global_maximum_marks_dirty(project):
    newest = project / "Assets" / "newest.txt"
    edited = project / "Assets" / "edited.cs"
    newest.write_text("newest", encoding="utf-8")
    edited.write_text("// old", encoding="utf-8")
    os.utime(newest, ns=(300_000_000_000, 300_000_000_000))
    os.utime(edited, ns=(100_000_000_000, 100_000_000_000))
    scanner = scanner_for(project)
    scanner.update_and_get("Test@generation")
    scanner.clear_dirty("Test@generation")
    scanner.update_and_get("Test@generation")
    edited.write_text("// changed and larger", encoding="utf-8")
    os.utime(edited, ns=(200_000_000_000, 200_000_000_000))
    assert scanner.update_and_get("Test@generation")["external_changes_dirty"] is True


def test_change_after_clear_before_next_scan_is_not_lost(project):
    target = project / "Assets" / "changed.cs"
    target.write_text("// baseline", encoding="utf-8")
    scanner = scanner_for(project)
    scanner.update_and_get("Test@generation")
    scanner.clear_dirty("Test@generation")
    target.write_text("// later change", encoding="utf-8")
    assert scanner.update_and_get("Test@generation")["external_changes_dirty"] is True


def test_incomplete_scan_is_never_reported_clean(project):
    (project / "Assets" / "unseen.cs").write_text("// source", encoding="utf-8")
    scanner = scanner_for(project, max_entries=1)
    assert scanner.update_and_get("Test@generation")["external_changes_dirty"] is True


def test_first_capture_requires_ack_before_it_is_clean(project):
    scanner = scanner_for(project)
    snapshot = scanner.capture("Test@generation")
    assert snapshot.reliable is True
    assert snapshot.dirty is True
    assert scanner.acknowledge("Test@generation", snapshot) is True
    assert scanner.capture("Test@generation").dirty is False


def test_acknowledgement_keeps_changes_after_capture(project):
    target = project / "Assets" / "changed.cs"
    target.write_text("// version one", encoding="utf-8")
    scanner = scanner_for(project)
    captured = scanner.capture("Test@generation")
    target.write_text("// version two is newer", encoding="utf-8")
    assert scanner.acknowledge("Test@generation", captured) is True
    current = scanner.capture("Test@generation")
    assert current.dirty is True
    assert dict(current.generations)["scripts"] > dict(captured.generations)["scripts"]


def test_script_acknowledgement_does_not_acknowledge_assets(project):
    scanner = scanner_for(project)
    assert scanner.acknowledge("Test@generation", scanner.capture("Test@generation"))
    (project / "Assets" / "new.cs").write_text("// source", encoding="utf-8")
    (project / "Assets" / "new.txt").write_text("asset", encoding="utf-8")
    scripts = scanner.capture("Test@generation", "scripts")
    assert scanner.acknowledge("Test@generation", scripts)
    assert scanner.capture("Test@generation", "scripts").dirty is False
    assert scanner.capture("Test@generation", "assets").dirty is True


def test_snapshot_from_another_scanner_epoch_cannot_acknowledge(project):
    first = scanner_for(project)
    captured = first.capture("Test@generation")
    second = scanner_for(project)
    assert second.acknowledge("Test@generation", captured) is False
    assert second.capture("Test@generation").dirty is True


def test_capture_completes_a_scan_larger_than_ordinary_budget(project):
    (project / "Assets" / "source.cs").write_text("// source", encoding="utf-8")
    scanner = scanner_for(project, max_entries=1)
    assert scanner.update_and_get("Test@generation")["external_changes_scan_complete"] is False
    captured = scanner.capture("Test@generation")
    assert captured.reliable is True
    assert scanner.acknowledge("Test@generation", captured) is True
    assert scanner.capture("Test@generation").dirty is False
    ordinary = scanner.update_and_get("Test@generation")
    assert ordinary["external_changes_scan_complete"] is True
    assert ordinary["external_changes_dirty"] is False


def test_unknown_project_root_cannot_produce_a_clean_snapshot():
    scanner = ExternalChangesScanner(scan_interval_ms=0)
    snapshot = scanner.capture("Unknown@instance")
    assert snapshot.reliable is False
    assert snapshot.dirty is True
    assert scanner.acknowledge("Unknown@instance", snapshot) is False


def test_changed_project_root_invalidates_existing_snapshot(project, tmp_path):
    scanner = scanner_for(project)
    captured = scanner.capture("Test@generation")
    other = tmp_path / "OtherProject"
    (other / "Assets").mkdir(parents=True)
    scanner.set_project_root("Test@generation", str(other))
    assert scanner.acknowledge("Test@generation", captured) is False
    assert scanner.capture("Test@generation").dirty is True


@pytest.mark.parametrize("relative, contents", [
    ("ProjectSettings/ProjectSettings.asset", "scriptingDefineSymbols: CHANGED"),
    ("Packages/manifest.json", '{"dependencies": {}}'),
    ("Packages/packages-lock.json", '{"dependencies": {}}'),
    ("Packages/com.example/package.json", '{"name": "com.example", "version": "1.0.0"}'),
])
def test_project_and_package_inputs_invalidate_both_scopes(project, relative, contents):
    scanner = scanner_for(project)
    assert scanner.acknowledge("Test@generation", scanner.capture("Test@generation"))
    target = project / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(contents, encoding="utf-8")
    assert scanner.capture("Test@generation", "scripts").dirty is True
    assert scanner.capture("Test@generation", "assets").dirty is True
