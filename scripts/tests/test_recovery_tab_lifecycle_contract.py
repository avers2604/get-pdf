"""GD-31J.27ar: offline source-only tab lifecycle contract, not a GUI/E2E test."""
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
WINDOW = ROOT / "src/GetDocument.Desktop/MainWindow.cs"
PANE = ROOT / "src/GetDocument.Desktop/Recovery/RecoveryDiagnosticsPane.cs"


class RecoveryTabLifecycleSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.window = WINDOW.read_text(encoding="utf-8")
        cls.pane = PANE.read_text(encoding="utf-8")
        cls.close = cls.window.split("private void CloseRecoveryDiagnostics(", 1)[1].split(
            "private void ShowRevisionHistory(", 1
        )[0]
        cls.source_close = cls.window.split("private void CloseDocument(Guid id)", 1)[1].split(
            "public void OpenFromLaunch(", 1
        )[0]

    def test_closing_diagnostics_restores_its_source_not_arbitrary_home_or_neighbor(self):
        self.assertIn("bool wasSelected = ReferenceEquals(_tabs.SelectedItem, view);", self.close)
        self.assertIn("if (!wasSelected) return;", self.close)
        self.assertIn("_views.TryGetValue(sourceId, out var sourceTab)", self.close)
        self.assertIn("_tabs.SelectedItem = sourceTab;", self.close)
        self.assertLess(self.close.index("bool wasSelected"), self.close.index("_items.Remove(view);"))

    def test_closing_a_source_never_reselects_its_closing_tab(self):
        self.assertIn("CloseRecoveryDiagnostics(id, restoreSourceSelection: false);",
                      self.source_close)
        self.assertLess(self.source_close.index("_workspace.Close(id);"),
                        self.source_close.index("CloseRecoveryDiagnostics(id,"))
        self.assertIn("if (!restoreSourceSelection)", self.close)
        self.assertIn("_workspace.ActiveId", self.close)

    def test_switching_away_cancels_stale_background_scan(self):
        self.assertIn("inspection.SuspendInspection();", self.window)
        self.assertIn("inspection.ResumeInspection();", self.window)
        self.assertIn("_activeInspection?.Cancel();", self.pane)
        self.assertIn("_suspended = true;", self.pane)
        self.assertIn("_suspended = false;", self.pane)

    def test_return_while_old_scan_cancels_retries_once(self):
        for marker in (
            "private bool _resumePending;",
            "if (_busy)",
            "_resumePending = true;",
            "if (_resumePending && !_disposed && !_suspended)",
            "_resumePending = false;",
            "_ = RefreshAsync();",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.pane)
        self.assertIn("_resumePending = false;", self.pane)

    def test_stale_scan_never_renders_after_deselection_or_dispose(self):
        self.assertIn("if (_disposed || _suspended || token.IsCancellationRequested) return;",
                      self.pane)
        self.assertIn("if (_disposed || _busy || _suspended) return;", self.pane)
        self.assertIn("CancellationTokenSource.CreateLinkedTokenSource(", self.pane)
        self.assertIn("using var inspection = CancellationTokenSource.CreateLinkedTokenSource(", self.pane)
        self.assertIn("_lifetime.Cancel();", self.pane)

    def test_no_extra_refresh_on_tab_creation_or_file_access_rights(self):
        self.assertNotIn("_ = pane.RefreshAsync();", self.window)
        self.assertIn("new RecoveryDiagnosticsPane(path)", self.window)
        self.assertEqual(1, self.pane.count("readonly Button "))
        for forbidden in ("File.Delete(", "File.Move(", "File.Write",
                          "CaptureAsync(", "Discard(", "Restore("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.pane)

    def test_closing_foreign_tab_does_not_steal_keyboard_focus(self):
        self.assertIn("if (!wasSelected) return;", self.close)
        self.assertIn("if (restoreSourceSelection &&", self.close)
        self.assertIn("_tabs.SelectedItem = sourceTab;", self.close)
        self.assertIn("CloseRecoveryDiagnostics(recovery.Key);", self.window)


if __name__ == "__main__":
    unittest.main()
