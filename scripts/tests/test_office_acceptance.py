"""Unit tests for the optional, fully offline local GD-31I acceptance runner."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import office_acceptance as acceptance


class AcceptancePlanTests(unittest.TestCase):
    def test_only_local_feed_used_and_all_suites_enforced(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/approved/feed"), "Windows")
        restore = next(step for step in steps if step.name == "solution-restore")
        self.assertEqual(
            ["dotnet", "restore", "GetDocument.slnx", "--source", "/approved/feed",
             "-p:NuGetAudit=false"],
            restore.command)
        for item in steps:
            if item.command[:2] == ["dotnet", "restore"]:
                self.assertIn("-p:NuGetAudit=false", item.command)
        self.assertEqual(1, len([s for s in steps if s.name == "solution-build"]))
        self.assertTrue(any(s.name == "windows-print-worker-build" for s in steps))
        suites = [s for s in steps if s.name.startswith("test-")]
        self.assertEqual(set(acceptance.TEST_PROJECTS.values()) | {"tests/GetPdf.Tests/GetPdf.Tests.csproj"},
                         {s.project for s in suites})
        for step in suites:
            self.assertIn("--minimum-expected-tests", step.command)
            self.assertIn("--no-restore", step.command)
        self.assertNotIn("http", " ".join(" ".join(step.command) for step in steps).lower())

    def test_real_wayland_cef_osr_contract_remains_opt_in_and_deferred(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-wayland-osr-source-contract-tests", names)
        self.assertLess(names.index("cef-wayland-osr-source-contract-tests"),
                        names.index("solution-restore"))
        self.assertIn("test_osr_capture_contract.py",
            next(x for x in steps if x.name == "cef-wayland-osr-source-contract-tests").command)

    def test_native_frame_transport_contract_is_deferred_to_local_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [s.name for s in steps]
        self.assertIn("native-frame-shell-contract-tests", names)
        self.assertLess(names.index("native-frame-shell-contract-tests"),
                        names.index("solution-restore"))
        check = next(s for s in steps if s.name == "native-frame-shell-contract-tests")
        self.assertIn("test_native_frame_shell_contract.py", check.command)

    def test_native_session_dispatch_contract_is_checked_before_restore(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("native-session-dispatch-contract-tests", names)
        self.assertLess(names.index("native-session-dispatch-contract-tests"),
                        names.index("solution-restore"))
        step = next(s for s in steps if s.name == "native-session-dispatch-contract-tests")
        self.assertIn("test_native_session_dispatch_contract.py", step.command)

    def test_native_avalonia_tabs_have_deferred_source_contract(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [s.name for s in steps]
        self.assertIn("native-avalonia-tab-contract-tests", names)
        self.assertLess(names.index("native-avalonia-tab-contract-tests"),
                        names.index("solution-restore"))
        contract = next(s for s in steps if s.name == "native-avalonia-tab-contract-tests")
        self.assertIn("test_native_tab_shell_contract.py", contract.command)

    def test_qt_ipc_static_contract_is_checked_without_running_qt(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [item.name for item in steps]
        self.assertIn("qt-ipc-contract-tests", names)
        self.assertLess(names.index("qt-ipc-contract-tests"),
                        names.index("solution-restore"))
        step = next(item for item in steps if item.name == "qt-ipc-contract-tests")
        self.assertIn("test_qt_ipc_contract.py", step.command)

    def test_native_sdk_event_contract_will_be_checked_in_local_acceptance(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [item.name for item in steps]
        self.assertIn("native-event-contract-tests", names)
        self.assertLess(names.index("native-event-contract-tests"),
                        names.index("solution-restore"))
        step = next(item for item in steps if item.name == "native-event-contract-tests")
        self.assertIn("test_native_event_contract.py", step.command)

    def test_qt_display_evidence_validator_is_checked_before_dotnet(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("qt-display-evidence-tests", names)
        self.assertLess(names.index("qt-display-evidence-tests"),
                        names.index("solution-restore"))
        evidence = next(item for item in steps if item.name == "qt-display-evidence-tests")
        self.assertNotIn("--display-live", evidence.command)
        self.assertIn("test_qt_display_evidence.py", evidence.command)

    def test_empty_view_source_contract_is_checked_offline_and_never_runs_cef(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-empty-view-contract-tests", names)
        self.assertLess(names.index("cef-empty-view-contract-tests"),
                        names.index("solution-restore"))
        step = next(s for s in steps if s.name == "cef-empty-view-contract-tests")
        self.assertNotIn("--cef-empty-view-lab", step.command)
        self.assertIn("test_empty_view_contract.py", step.command)

    def test_cef_lab_preflight_suite_runs_before_any_office_restore(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        names = [step.name for step in steps]
        self.assertIn("cef-lab-offline-tests", names)
        self.assertLess(names.index("cef-lab-offline-tests"),
                        names.index("solution-restore"))

    def test_native_probe_test_double_runs_before_dotnet_restore(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Windows")
        names = [step.name for step in steps]
        self.assertIn("native-probe-mock-tests", names)
        self.assertLess(names.index("native-probe-mock-tests"),
                        names.index("solution-restore"))

    def test_compiled_qt_host_is_a_distinct_optional_gate(self):
        qt_binary = Path("/approved/build/getdoc-eurooffice-qt-probe")
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux",
                                       qt_host_binary=qt_binary)
        checks = [step for step in steps if step.name == "native-qt-headless-smoke"]
        self.assertEqual(1, len(checks))
        self.assertEqual(str(qt_binary), checks[0].command[-1])
        self.assertLess([step.name for step in steps].index("native-qt-headless-smoke"),
                        [step.name for step in steps].index("solution-restore"))
        display = [step for step in steps if step.name == "native-qt-display-offscreen"]
        self.assertEqual(1, len(display))
        self.assertIn("test_qt_display.py", display[0].command)
        self.assertEqual(str(qt_binary), display[0].command[-1])
        ipc = [step for step in steps if step.name == "native-qt-tab-ipc"]
        self.assertEqual(1, len(ipc))
        self.assertIn("test_qt_ipc.py", ipc[0].command)
        self.assertEqual(str(qt_binary), ipc[0].command[-1])
        self.assertLess([step.name for step in steps].index("native-qt-tab-ipc"),
                        [step.name for step in steps].index("solution-restore"))

    def test_linux_plan_does_not_assume_windows_only_build_tools(self):
        steps = acceptance.build_steps(Path("/get-document"), Path("/feed"), "Linux")
        self.assertFalse(any(step.name.startswith("windows-") for step in steps))
        self.assertFalse(any("GetPdf.slnx" in step.command for step in steps))
        self.assertIn("solution-build", [s.name for s in steps])

    def test_automatic_failure_blocks_all_following_steps(self):
        steps = [
            acceptance.CommandStep("restore", ["dotnet", "restore", "GetDocument.slnx"]),
            acceptance.CommandStep("build", ["dotnet", "build", "GetDocument.slnx"]),
            acceptance.CommandStep("test", ["dotnet", "test", "x.csproj"])
        ]
        invoked = []

        def invoke(step):
            invoked.append(step.name)
            return 23 if step.name == "build" else 0

        results, passed = acceptance.run_steps(steps, invoke)
        self.assertFalse(passed)
        self.assertEqual(["restore", "build"], invoked)
        self.assertEqual(["passed", "failed", "not-run"], [s["status"] for s in results])

    def test_refuses_dirty_worktree_so_git_sha_identifies_tested_sources(self):
        with patch.object(acceptance.subprocess, "run", side_effect=[
            SimpleNamespace(stdout="a" * 40 + "\n"),
            SimpleNamespace(stdout=" M src/GetDocument.Office/LibreOffice/lo_editor.py\n")
        ]):
            with self.assertRaises(RuntimeError):
                acceptance.git_revision(Path("/repo"))

    def test_rejects_preview_sdk_for_release_gate(self):
        with patch.object(acceptance.shutil, "which", return_value="/bin/dotnet"):
            with patch.object(acceptance.subprocess, "run",
                              return_value=SimpleNamespace(stdout="10.0.100-preview.1\n")):
                with self.assertRaises(RuntimeError):
                    acceptance.ensure_dotnet10()

    def test_empty_plan_is_not_an_automatic_success(self):
        results, passed = acceptance.run_steps([], lambda _: 0)
        self.assertEqual([], results)
        self.assertFalse(passed)

    def test_report_always_requires_manual_acceptance(self):
        report = acceptance.new_report("abc123", "Windows", "GD-31I")
        report["automatedChecksPassed"] = True
        report["status"] = "automated-checks-passed"
        self.assertFalse(report["releaseReady"])
        self.assertTrue(all(value == "not-run" for value in report["manualAcceptance"].values()))


class SourceChecksTests(unittest.TestCase):
    def test_python_whitelist_and_csharp_catalog_match_exactly(self):
        catalog = 'new("calc-one", "one", "calc", true),\nnew("writer-two", "two", "writer", true)'
        python = 'FEATURES = {\n    "calc-one": ("calc", True),\n    "writer-two": ("writer", True),\n}\n'
        self.assertEqual(["calc-one", "writer-two"], acceptance.compare_feature_catalogs(catalog, python))
        with self.assertRaises(ValueError):
            acceptance.compare_feature_catalogs(catalog, python.replace("writer-two", "hidden-feature"))
        with self.assertRaises(ValueError):
            acceptance.compare_feature_catalogs(catalog, python.replace('    "writer-two": ("writer", True),', ''))

    def test_invalid_embedded_python_fails_preflight(self):
        with self.assertRaises(SyntaxError):
            acceptance.check_python_syntax("def bad(:\n  pass")

    def test_runner_config_requires_microsoft_testing_platform(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "global.json").write_text('{"test":{"runner":"VSTest"}}', encoding="utf-8")
            with self.assertRaises(ValueError):
                acceptance.check_test_runner(root)
            (root / "global.json").write_text(
                '{"test":{"runner":"Microsoft.Testing.Platform"}}', encoding="utf-8")
            acceptance.check_test_runner(root)

    def test_report_evidence_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "existing-report.json"
            path.write_text('{"original":"approved-evidence"}', encoding="utf-8")
            with self.assertRaises(FileExistsError):
                acceptance.write_report(path, acceptance.new_report("abc", "Windows", "GD-31I"))
            self.assertEqual('{"original":"approved-evidence"}',
                             path.read_text(encoding="utf-8"))

    def test_json_report_is_atomic_and_has_no_release_approval(self):
        with tempfile.TemporaryDirectory() as temp:
            report = acceptance.new_report("012345", "Linux", "GD-31I")
            path = Path(temp) / "report.json"
            acceptance.write_report(path, report)
            saved = json.loads(path.read_text(encoding="utf-8"))
            self.assertFalse(saved["releaseReady"])
            self.assertEqual("012345", saved["revision"])
            self.assertFalse(path.with_suffix(".json.tmp").exists())


if __name__ == "__main__":
    unittest.main()
