"""GD-31J.27at: stacked recovery-contract integrity checks (source-only).

These tests guard future regressions in ordering and registration; they are
NOT .NET compilation, GUI automation, or Windows/Astra release evidence.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
PANE = ROOT / "src/GetDocument.Desktop/Recovery/RecoveryDiagnosticsPane.cs"
WINDOW = ROOT / "src/GetDocument.Desktop/MainWindow.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"
TESTS = ROOT / "scripts/tests"
REQUIRED = {
    "recovery-readonly-inventory-source-tests":
        "test_recovery_readonly_inventory_contract.py",
    "recovery-diagnostics-ui-source-tests":
        "test_recovery_diagnostics_ui_contract.py",
    "recovery-diagnostics-a11y-source-tests":
        "test_recovery_diagnostics_a11y_contract.py",
    "recovery-tab-lifecycle-source-tests":
        "test_recovery_tab_lifecycle_contract.py",
    "recovery-reopen-shutdown-source-tests":
        "test_recovery_reopen_shutdown_contract.py",
    "recovery-chain-consistency-source-tests":
        "test_recovery_chain_consistency.py",
}


class RecoveryChainIntegrity(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pane = PANE.read_text(encoding="utf-8")
        cls.window = WINDOW.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.refresh = cls.pane.split(
            "internal async Task RefreshAsync()", 1
        )[1].split("private void Render(", 1)[0]
        cls.finally_body = cls.refresh.split("finally\n        {", 1)[1]

    def test_dispose_child_linked_token_before_parent_on_shutdown(self):
        # Using declarations are disposed only when the method exits, AFTER
        # its finally. Disposing the shared parent inside that finally first
        # breaks the intended linked-token lifetime ordering.
        self.assertNotIn("using var inspection =", self.refresh)
        self.assertIn("var inspection = CancellationTokenSource.CreateLinkedTokenSource(",
                      self.refresh)
        self.assertIn("inspection.Dispose();", self.finally_body)
        self.assertIn("DisposeLifetime();", self.finally_body)
        self.assertLess(self.finally_body.index("inspection.Dispose();"),
                        self.finally_body.index("DisposeLifetime();"))
        self.assertIn("if (ReferenceEquals(_activeInspection, inspection))",
                      self.finally_body)
        self.assertIn("_activeInspection = null;", self.finally_body)

    def test_cancelled_or_closed_scan_never_publishes_stale_results(self):
        self.assertIn("if (_disposed || _suspended || token.IsCancellationRequested) return;",
                      self.refresh)
        self.assertIn("_activeInspection?.Cancel();", self.pane)
        self.assertIn("_lifetime.Cancel();", self.pane)
        self.assertNotIn(".Wait();", self.refresh)
        self.assertNotIn("Task.WaitAll(", self.refresh)

    def test_every_recovery_source_contract_is_registered_once_before_restore(self):
        for name, file_name in REQUIRED.items():
            with self.subTest(name=name):
                self.assertTrue((TESTS / file_name).is_file())
                self.assertEqual(1, self.runner.count('CommandStep("' + name + '"'))
                self.assertLess(self.runner.index('CommandStep("' + name + '"'),
                                self.runner.index('CommandStep("solution-restore"'))
                self.assertIn('"-p", "' + file_name + '"', self.runner)

    def test_registered_recovery_manual_gates_are_unique_and_not_automated(self):
        all_gates = re.findall(r'    "(gd31j27a[lnopqrst][^"]+-manual-e2e)",',
                               self.runner)
        self.assertEqual(len(all_gates), len(set(all_gates)))
        self.assertGreaterEqual(len(all_gates), 8)
        self.assertIn("def new_report(", self.runner)
        self.assertIn('"manualAcceptance": {name: "not-run" for name in MANUAL_GATES}', self.runner)
        self.assertIn('"releaseReady": False', self.runner)

    def test_no_retired_close_api_assertion_in_active_recovery_tests(self):
        for file_name in (
            "test_recovery_diagnostics_ui_contract.py",
            "test_recovery_diagnostics_a11y_contract.py",
            "test_recovery_tab_lifecycle_contract.py",
            "test_recovery_reopen_shutdown_contract.py",
        ):
            with self.subTest(file_name=file_name):
                content = (TESTS / file_name).read_text(encoding="utf-8")
                self.assertNotIn('self.assertIn("CloseRecoveryDiagnostics(id);"', content)
        self.assertIn("CloseRecoveryDiagnostics(id, restoreSourceSelection: false);",
                      self.window)

    def test_old_ui_contract_uses_per_inspection_token_not_disposed_parent(self):
        legacy = (TESTS / "test_recovery_diagnostics_ui_contract.py").read_text(
            encoding="utf-8")
        self.assertNotIn('CancellationToken token = _lifetime.Token;', legacy)
        self.assertIn('CancellationToken token = inspection.Token;', legacy)
        self.assertIn('CreateLinkedTokenSource(', self.pane)

    def test_no_new_user_data_operations_in_diagnostics_pane(self):
        for forbidden in (
            "File.Delete(", "File.Move(", "File.Read", "File.Write",
            "Directory.Delete(", "Directory.CreateDirectory(", "CaptureAsync(",
            "Discard(", "Restore(", "Process.Start(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.pane)
        self.assertEqual(1, self.pane.count("readonly Button "))


if __name__ == "__main__":
    unittest.main()
