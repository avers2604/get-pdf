#!/usr/bin/env python3
"""GD-31I: offline-only, reproducible LOCAL acceptance runner for GET Documents.

This is a developer/IT tool, NOT a production dependency or a release approval.
Never downloads tools, connects to the Internet, enables CI, or edits documents.
Use --feed <approved local NuGet folder>; --plan never launches build tools.
"""
from __future__ import annotations

import argparse
import ast
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import time
import uuid


REPO_ROOT = Path(__file__).resolve().parents[1]

TEST_PROJECTS = {
    "core": "tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj",
    "office": "tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj",
    "images": "tests/GetDocument.Images.Tests/GetDocument.Images.Tests.csproj",
    "revisions": "tests/GetDocument.Revisions.Tests/GetDocument.Revisions.Tests.csproj",
    "collaboration": "tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj",
    "wopi-gateway": "tests/GetDocument.WopiGateway.Tests/GetDocument.WopiGateway.Tests.csproj",
}
# These are genuinely manual/hardware/backend gates, NOT inferred from green MSTest.
MANUAL_GATES = (
    "windows-get-desktop-editor-and-tabs",
    "linux-astra-desktop-and-dpi",
    "real-libreoffice-uno-writeback-and-xlsx-roundtrip",
    "eurooffice-desktop-embedding-api",
    "eurooffice-native-sdk-fixture-binding-and-import-e2e",
    "eurooffice-native-converter-private-artifact-candidate-e2e",
    "eurooffice-content-ready-patched-sdk-runtime-e2e",
    "eurooffice-synthetic-private-save-roundtrip-and-immutable-fixture-e2e",
    "eurooffice-guarded-sameformat-saveas-event-and-private-path-e2e",
    "eurooffice-saveas-seven-argument-route-duplicate-conflict-timeout-e2e",
    "eurooffice-saveas-v2-native-event-order-failure-codes-e2e",
    "eurooffice-gd31j29-synthetic-reviewed-no-replace-hardlink-durability-vm",
    "eurooffice-gd31j30-journal-crash-orphan-detection-no-cleanup-vm",
    "eurooffice-gd31j31-private-redacted-recovery-report-manual-vm",
    "eurooffice-gd31j32-fault-boundary-exception-vm",
    "eurooffice-gd31j32-real-kill-powerloss-directory-fsync-vm",
    "eurooffice-gd31j33-net10-windows-linux-hardlink-build-and-no-replace-vm",
    "eurooffice-gd31j34-cef-duplicate-after-target-v2-receipt-manual-vm",
    "eurooffice-gd31j35-docx-xlsx-pptx-manifest-and-roundtrip-manual-vm",
    "eurooffice-gd31j36-native-saveas-v2-stdout-2048-4096-manual-vm",
    "eurooffice-gd31j37-post-onsave-timeout-and-strict-manifest-vm",
    "eurooffice-gd31j38-offline-feed-windows-linux-build-replay-manual",
    "eurooffice-cef-authenticated-caret-ime-candidates",
    "eurooffice-windows-x11-osr-or-uno-fallback",
    "eurooffice-server-2-5-10-user-coediting",
    "corporate-sso-acl-and-worm-cas",
    "smb-conflicts-and-emergency-recovery",
    "office-native-save-conflict-and-no-overwrite-copy",
    "physical-get-print-wizard-a4-a3",
    "ms-office-file-compatibility-check",
    "licensing-signing-and-offline-installer",
)


@dataclass(frozen=True)
class CommandStep:
    name: str
    command: list[str]
    project: str | None = None


def build_steps(root: Path, feed: Path, host: str,
                qt_host_binary: Path | None = None) -> list[CommandStep]:
    """Offline restore is the ONLY allowed NuGet source; never guess a remote URL."""
    _ = root  # Callers and subprocess always operate at the checked-out repo root.
    steps = [
        CommandStep("runner-self-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests", "-p", "test_office_acceptance.py"]),
        CommandStep("gd31j38-plan-inventory-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_gd31j38_acceptance_plan_contract.py"]),
        CommandStep("office-native-save-receipt-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_lo_save_receipt_contract.py"]),
        CommandStep("native-probe-mock-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop", "-p", "test_mock_host.py"]),
        CommandStep("cef-lab-offline-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_cef_lab_gate.py"]),
        CommandStep("cef-empty-view-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_empty_view_contract.py"]),
        CommandStep("qt-display-evidence-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop",
                     "-p", "test_qt_display_evidence.py"]),
        CommandStep("native-event-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_event_contract.py"]),
        CommandStep("qt-ipc-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop",
                     "-p", "test_qt_ipc_contract.py"]),
        CommandStep("native-avalonia-tab-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "scripts/tests", "-p", "test_native_tab_shell_contract.py"]),
        CommandStep("native-session-dispatch-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "scripts/tests", "-p", "test_native_session_dispatch_contract.py"]),
        CommandStep("native-frame-shell-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "scripts/tests", "-p", "test_native_frame_shell_contract.py"]),
        CommandStep("cef-net10-hardlink-api-compatibility-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_net10_hardlink_compatibility_contract.py"]),
        CommandStep("cef-synthetic-fault-checkpoint-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_synthetic_fault_checkpoint_contract.py"]),
        CommandStep("cef-synthetic-recovery-report-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_synthetic_recovery_report_contract.py"]),
        CommandStep("cef-synthetic-snapshot-journal-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_synthetic_snapshot_journal_contract.py"]),
        CommandStep("cef-synthetic-terminal-manifest-contract-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_synthetic_terminal_manifest_contract.py"]),
        CommandStep("cef-saveas-v2-transport-bound-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_saveas_v2_transport_bound_contract.py"]),
        CommandStep("cef-synthetic-three-fixture-manifest-integrity-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_synthetic_manifest_integrity_contract.py"]),
        CommandStep("cef-synthetic-reviewed-snapshot-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_synthetic_reviewed_snapshot_contract.py"]),
        CommandStep("cef-native-saveas-v2-event-receipt-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_event_receipt_contract.py"]),
        CommandStep("cef-native-saveas-lifecycle-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_saveas_lifecycle_contract.py"]),
        CommandStep("cef-guarded-native-saveas-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_guarded_native_saveas_contract.py"]),
        CommandStep("cef-private-save-roundtrip-source-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_private_roundtrip_contract.py"]),
        CommandStep("cef-native-ready-overlay-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_ready_bridge_overlay.py"]),
        CommandStep("cef-native-ready-bridge-source-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_ready_bridge_contract.py"]),
        CommandStep("cef-native-conversion-evidence-source-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_conversion_evidence_contract.py"]),
        CommandStep("cef-native-binding-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_native_binding_contract.py"]),
        CommandStep("cef-wayland-osr-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_capture_contract.py"]),
        CommandStep("cef-osr-stream-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "scripts/tests", "-p", "test_osr_stream_contract.py"]),
        CommandStep("cef-osr-input-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_input_contract.py"]),
        CommandStep("cef-osr-click-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_click_contract.py"]),
        CommandStep("cef-osr-keyboard-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_keyboard_contract.py"]),
        CommandStep("cef-osr-ime-commit-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_osr_ime_commit_contract.py"]),
        CommandStep("eurooffice-stacked-contract-integrity-tests",
                    [sys.executable, "-m", "unittest", "discover",
                     "-s", "scripts/tests",
                     "-p", "test_eurooffice_stacked_contract_integrity.py"]),
        CommandStep("avalonia-ime-caret-source-contract-tests",
                    [sys.executable, "-m", "unittest", "discover", "-s",
                     "probes/eurooffice-desktop/cef_lab",
                     "-p", "test_avalonia_ime_caret_contract.py"]),
        CommandStep("solution-restore",
                    ["dotnet", "restore", "GetDocument.slnx", "--source", str(feed), "-p:NuGetAudit=false"]),
        CommandStep("solution-build",
                    ["dotnet", "build", "GetDocument.slnx", "-c", "Release", "--no-restore"]),
    ]
    if qt_host_binary is not None:
        # This is a REAL local compiled Qt process test, never a mock. It
        # advertises zero file permissions and does not load CEF or documents.
        steps.insert(2, CommandStep("native-qt-headless-smoke", [
            sys.executable, "probes/eurooffice-desktop/test_qt_host.py",
            str(qt_host_binary)
        ]))
        steps.insert(3, CommandStep("native-qt-display-offscreen", [
            sys.executable, "probes/eurooffice-desktop/test_qt_display.py",
            str(qt_host_binary)
        ]))
        steps.insert(4, CommandStep("native-qt-tab-ipc", [
            sys.executable, "probes/eurooffice-desktop/test_qt_ipc.py",
            str(qt_host_binary)
        ]))
    for label, project in TEST_PROJECTS.items():
        steps.append(CommandStep(
            "test-" + label,
            ["dotnet", "test", project, "-c", "Release", "--no-restore",
             "--minimum-expected-tests", "1"],
            project=project))
    if host == "Windows":
        worker = "src/GetDocument.PrintWindows/GetDocument.PrintWindows.csproj"
        steps.extend([
            CommandStep("windows-print-worker-restore",
                        ["dotnet", "restore", worker, "--source", str(feed), "-p:NuGetAudit=false"]),
            CommandStep("windows-print-worker-build",
                        ["dotnet", "build", worker, "-c", "Release", "--no-restore"]),
            CommandStep("windows-legacy-solution-restore",
                        ["dotnet", "restore", "GetPdf.slnx", "--source", str(feed), "-p:NuGetAudit=false"]),
            CommandStep("windows-legacy-solution-build",
                        ["dotnet", "build", "GetPdf.slnx", "-c", "Release", "--no-restore"]),
            CommandStep("test-legacy-pdf",
                        ["dotnet", "test", "tests/GetPdf.Tests/GetPdf.Tests.csproj",
                         "-c", "Release", "--no-restore", "--minimum-expected-tests", "1"],
                        project="tests/GetPdf.Tests/GetPdf.Tests.csproj")
        ])
    return steps



def validate_step_plan(root: Path, steps: list[CommandStep]) -> list[str]:
    """Validate the ENTIRE offline command inventory without invoking commands.

    This does not prove an SDK or package exists, and it cannot substitute for
    disconnecting the lab network. It prevents typoed/missing tests or silent
    insertion of shell/online commands from being treated as a verified plan.
    """
    base = root.resolve()
    if not steps:
        raise ValueError("Offline acceptance has no steps.")
    seen: set[str] = set()

    def local_file(relative: str) -> Path:
        candidate = Path(relative)
        if candidate.is_absolute() or not relative or "://" in relative:
            raise ValueError("Acceptance target must be a relative file path.")
        full = (base / candidate).resolve()
        if not full.is_relative_to(base) or not full.is_file():
            if not full.is_relative_to(base):
                raise ValueError("Acceptance target escapes repository.")
            raise FileNotFoundError("Missing declared local acceptance target: " + relative)
        return full

    for step in steps:
        if not step.name or step.name in seen:
            raise ValueError("Duplicate or unnamed acceptance step.")
        seen.add(step.name)
        cmd = step.command
        if not cmd or not all(isinstance(x, str) and x for x in cmd):
            raise ValueError("Empty acceptance command.")
        if cmd[0] == sys.executable:
            if len(cmd) == 8 and cmd[1:4] == ["-m", "unittest", "discover"] \
                    and cmd[4] == "-s" and cmd[6] == "-p":
                directory, pattern = cmd[5], cmd[7]
                if pattern != Path(pattern).name or not pattern.startswith("test_") \
                        or not pattern.endswith(".py") or any(c in pattern for c in "*?[]"):
                    raise ValueError("Unapproved Python test pattern.")
                local_file(str(Path(directory) / pattern))
            elif len(cmd) == 3 and cmd[1] in (
                "probes/eurooffice-desktop/test_qt_host.py",
                "probes/eurooffice-desktop/test_qt_display.py",
                "probes/eurooffice-desktop/test_qt_ipc.py",
            ):
                local_file(cmd[1])
                if "://" in cmd[2]:
                    raise ValueError("Qt host path must not be a URL.")
            else:
                raise ValueError("Unapproved Python command in acceptance plan.")
            if step.project is not None:
                raise ValueError("Python source tests must not supply a .NET project.")
        elif cmd[0] == "dotnet":
            if len(cmd) < 3 or cmd[1] not in ("restore", "build", "test"):
                raise ValueError("Unapproved .NET command in acceptance plan.")
            local_file(cmd[2])
            if not cmd[2].endswith((".slnx", ".csproj")):
                raise ValueError("Only local solution/project targets are accepted.")
            operation = cmd[1]
            if operation == "restore":
                if len(cmd) != 6 or cmd[3] != "--source" or \
                        cmd[5] != "-p:NuGetAudit=false":
                    raise ValueError("Restore must use one explicit approved offline feed.")
                feed = cmd[4]
                if "://" in feed or feed.startswith("\\\\") or \
                        (not Path(feed).is_absolute() and feed != "<approved-offline-feed>"):
                    raise ValueError("Remote/implicit package source is forbidden.")
                if step.project is not None:
                    raise ValueError("Restore cannot be presented as a test project.")
            elif operation == "build":
                if cmd[3:] != ["-c", "Release", "--no-restore"] or \
                        step.project is not None:
                    raise ValueError("Build must reuse the approved offline restore.")
            else:
                if cmd[3:] != ["-c", "Release", "--no-restore",
                               "--minimum-expected-tests", "1"] or \
                        step.project != cmd[2] or not cmd[2].endswith(".csproj"):
                    raise ValueError("Test step must use offline MTP and a matching project.")
        else:
            raise ValueError("Only fixed Python and .NET commands are approved.")

    names = [item.name for item in steps]
    if "solution-restore" not in seen or "solution-build" not in seen:
        raise ValueError("Required restore/build barriers are absent.")
    restore = names.index("solution-restore")
    build = names.index("solution-build")
    if restore >= build:
        raise ValueError("Build cannot precede approved restore.")
    for i, step in enumerate(steps):
        if step.command[:2] == ["dotnet", "test"] and i <= build:
            raise ValueError("Automated .NET tests must follow the solution build.")
        if step.command[:2] == ["dotnet", "build"]:
            if not any(previous.command[:2] == ["dotnet", "restore"] and
                       previous.command[2] == step.command[2]
                       for previous in steps[:i]):
                raise ValueError("Project build has no earlier local restore.")
        if step.command[:2] == ["dotnet", "test"]:
            if not any(previous.command[:2] == ["dotnet", "restore"]
                       for previous in steps[:i]):
                raise ValueError("Project test has no preceding local restore.")
    return names


def run_steps(steps: list[CommandStep], invoke) -> tuple[list[dict], bool]:
    """Stop on the FIRST failing step. Never claim unexecuted steps passed."""
    if not steps:
        return [], False
    results = []
    failed = False
    for step in steps:
        if failed:
            results.append({"name": step.name, "status": "not-run", "exitCode": None})
            continue
        started = time.monotonic()
        try:
            code = int(invoke(step))
        except (OSError, subprocess.TimeoutExpired, ValueError):
            code = 127
        passed = code == 0
        results.append({
            "name": step.name,
            "status": "passed" if passed else "failed",
            "exitCode": code,
            "durationSeconds": round(time.monotonic() - started, 2),
        })
        if not passed:
            failed = True
    return results, not failed


def check_test_runner(root: Path) -> None:
    config = json.loads((root / "global.json").read_text(encoding="utf-8"))
    if config.get("test", {}).get("runner") != "Microsoft.Testing.Platform":
        raise ValueError("global.json must explicitly select Microsoft.Testing.Platform.")


def check_python_syntax(source: str) -> None:
    ast.parse(source, filename="lo_editor.py", mode="exec")


def compare_feature_catalogs(csharp: str, python: str) -> list[str]:
    catalog = re.findall(r'new\("((?:writer|calc|impress)-[a-z-]+)"', csharp)
    bridge = re.findall(
        r'^    "((?:writer|calc|impress)-[a-z-]+)": \("(?:writer|calc|impress)", (?:True|False)\),?$',
        python, flags=re.MULTILINE)
    if not catalog or len(catalog) != len(set(catalog)) or catalog != bridge:
        raise ValueError("OfficeFeatureCatalog.cs and lo_editor.py whitelist disagree.")
    return catalog


def source_preflight(root: Path) -> dict:
    if not (root / "GetDocument.slnx").is_file():
        raise FileNotFoundError("GetDocument.slnx is missing.")
    check_test_runner(root)
    office = root / "src/GetDocument.Office/LibreOffice"
    bridge = (office / "lo_editor.py").read_text(encoding="utf-8")
    check_python_syntax(bridge)
    check_python_syntax((office / "lo_bridge.py").read_text(encoding="utf-8"))
    catalog = (office / "OfficeFeatureCatalog.cs").read_text(encoding="utf-8")
    ids = compare_feature_catalogs(catalog, bridge)
    for project in TEST_PROJECTS.values():
        if not (root / project).is_file():
            raise FileNotFoundError("Required test project is missing: " + project)
    # Both host plans are checked even on a single-OS workstation. This
    # includes every deferred Python probe and Windows-only project target.
    linux_steps = validate_step_plan(
        root, build_steps(root, Path("<approved-offline-feed>"), "Linux"))
    windows_steps = validate_step_plan(
        root, build_steps(root, Path("<approved-offline-feed>"), "Windows"))
    return {"microsoftTestingPlatform": True, "pythonBridgeSyntax": True,
            "localFeatureCatalogMatch": True, "featureCount": len(ids),
            "offlineStepTargetsVerified": True,
            "linuxPlanStepCount": len(linux_steps),
            "windowsPlanStepCount": len(windows_steps)}


def new_report(revision: str, host: str, stage: str) -> dict:
    return {
        "schemaVersion": 1,
        "stage": stage,
        "revision": revision,
        "host": host,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "status": "blocked",
        "automatedChecksPassed": False,
        # Never make this True by a successful dotnet test: real editors, ACL and
        # hardware printing require independent human acceptance evidence.
        "releaseReady": False,
        "manualAcceptance": {name: "not-run" for name in MANUAL_GATES},
        "preflight": {},
        "steps": [],
    }


def write_report(path: Path, report: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temporary.open("x", encoding="utf-8") as output:
            json.dump(report, output, ensure_ascii=False, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        # A hard-link publishes a complete report atomically and FAILS if the
        # named evidence file already exists. Do not use os.replace here:
        # overwriting an earlier result would defeat the acceptance audit.
        os.link(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def git_revision(root: Path) -> str:
    result = subprocess.run(
        ["git", "rev-parse", "--verify", "HEAD"], cwd=root, text=True,
        capture_output=True, check=True, timeout=10)
    revision = result.stdout.strip()
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("No valid Git revision for this acceptance run.")
    dirty = subprocess.run(
        ["git", "status", "--porcelain", "--untracked-files=all"], cwd=root,
        text=True, capture_output=True, check=True, timeout=15)
    if dirty.stdout.strip():
        # A Git SHA cannot attest to modified, uncommitted code. Reports and
        # build outputs live in .gitignored artifacts/ and do not trip this gate.
        raise RuntimeError("The Git worktree contains uncommitted changes; "
                           "acceptance requires an exact committed revision.")
    return revision


def ensure_dotnet10() -> str:
    if not shutil.which("dotnet"):
        raise RuntimeError(".NET 10 SDK is not installed.")
    result = subprocess.run(["dotnet", "--version"], text=True,
                            capture_output=True, check=True, timeout=20)
    version = result.stdout.strip()
    if not re.fullmatch(r"10\.\d+\.\d+", version):
        raise RuntimeError("Expected .NET 10 SDK, found " + version)
    return version


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Local offline GET Documents acceptance.")
    parser.add_argument("--feed", type=Path,
                        help="Existing APPROVED offline NuGet directory (required for checks).")
    parser.add_argument("--plan", action="store_true", help="Inspect only; do not build/test.")
    parser.add_argument("--qt-host-binary", type=Path,
                        help="Locally compiled Qt host; enables REAL headless handshake smoke test.")
    parser.add_argument("--report", type=Path,
                        help="Output JSON path; default artifacts/local-acceptance/<unique>.json")
    args = parser.parse_args(argv)
    root = REPO_ROOT
    host = platform.system()
    report = new_report("unknown", host, "GD-31I")
    path = args.report or root / "artifacts" / "local-acceptance" / (
        datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" +
        uuid.uuid4().hex[:10] + ".json")
    result_code = 2
    try:
        report["revision"] = git_revision(root)
        report["preflight"] = source_preflight(root)
        if host not in ("Windows", "Linux"):
            raise RuntimeError("Only Windows and Linux test hosts are supported.")

        qt_binary = args.qt_host_binary.expanduser().resolve() if args.qt_host_binary else None
        if qt_binary is not None and not args.plan and not qt_binary.is_file():
            raise RuntimeError("Compiled local Qt host binary was not found.")

        if args.plan:
            planned = build_steps(root, Path("<approved-offline-feed>"), host,
                                  qt_host_binary=qt_binary)
            validate_step_plan(root, planned)
            report["status"] = "plan-only"
            report["steps"] = [
                {"name": item.name, "status": "not-run", "command": item.command}
                for item in planned
            ]
            result_code = 0
        else:
            if args.feed is None:
                raise RuntimeError("--feed is required; network restore is forbidden.")
            feed = args.feed.expanduser().resolve()
            if not feed.is_dir():
                raise RuntimeError("Approved local NuGet feed directory does not exist.")
            steps = build_steps(root, feed, host, qt_host_binary=qt_binary)
            validate_step_plan(root, steps)
            report["dotnetSdk"] = ensure_dotnet10()
            environment = os.environ.copy()
            environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
            environment["DOTNET_NOLOGO"] = "1"
            environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true"

            def invoke(step: CommandStep) -> int:
                print("[GD-31I]", step.name, flush=True)
                # Explicit argument array, no shell interpolation and no online
                # restore; logs remain in the current terminal, NOT in report.
                result = subprocess.run(step.command, cwd=root, env=environment,
                                        check=False, timeout=1800)
                return result.returncode

            report["steps"], passed = run_steps(steps, invoke)
            report["automatedChecksPassed"] = passed
            report["status"] = "automated-checks-passed" if passed else "failed"
            result_code = 0 if passed else 1
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError,
            subprocess.TimeoutExpired, json.JSONDecodeError, SyntaxError) as exc:
        report["status"] = "blocked"
        # No credentials, URLs or process output are copied into the report.
        report["blockingReason"] = type(exc).__name__ + ": " + str(exc)[:240]
        print("BLOCKED:", report["blockingReason"], file=sys.stderr)
    finally:
        write_report(path, report)
        print("GD-31I report:", path)
        print("Automatic:", report["status"])
        print("RELEASE APPROVED: NO — manual/hardware/backend gates remain NOT RUN.")
    return result_code


if __name__ == "__main__":
    raise SystemExit(main())
