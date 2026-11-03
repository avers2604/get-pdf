"""GD-31J.27as: offline source contracts. No GUI, SMB, or .NET runtime claims."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
WINDOW = ROOT / "src/GetDocument.Desktop/MainWindow.cs"
PANE = ROOT / "src/GetDocument.Desktop/Recovery/RecoveryDiagnosticsPane.cs"
MODEL_TESTS = ROOT / "tests/GetDocument.Core.Tests/DocumentWorkspaceTests.cs"


class RecoveryReopenShutdownContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.window = WINDOW.read_text(encoding="utf-8")
        cls.pane = PANE.read_text(encoding="utf-8")
        cls.model_tests = MODEL_TESTS.read_text(encoding="utf-8")
        cls.closed = cls.window.split("Closed += (_, _) =>", 1)[1].split(
            "Closed += async (_, _) =>", 1)[0]
        cls.attached = cls.pane.split("AttachedToVisualTree +=", 1)[1].split(
            "KeyDown += OnPaneKeyDown", 1)[0]
        cls.dispose = cls.pane.split("public void Dispose()", 1)[1]

    def test_shutdown_signals_inspections_before_other_disposables(self):
        self.assertIn("foreach (var inspection in _recoveryTabs.Values)", self.closed)
        self.assertLess(self.closed.index("foreach (var inspection in _recoveryTabs.Values)"),
                        self.closed.index("foreach (var view in _views.Values)"))
        self.assertNotIn("Task.WaitAll(", self.closed)
        self.assertNotIn(".Wait();", self.closed)
        self.assertNotIn(".GetAwaiter().GetResult()", self.closed)

    def test_shutdown_does_not_signal_cancelled_window_close(self):
        self.assertIn("Closing += async (_, args) =>", self.window)
        closing = self.window.split("Closing += async (_, args) =>", 1)[1].split(
            "private static bool HasUnsaved", 1)[0]
        self.assertNotIn("Dispose()", closing)
        self.assertNotIn("SuspendInspection()", closing)
        self.assertNotIn("Cancel()", closing)

    def test_cancel_does_not_dispose_shared_cts_while_scan_is_busy(self):
        self.assertIn("_activeInspection?.Cancel();", self.dispose)
        self.assertIn("_lifetime.Cancel();", self.dispose)
        self.assertIn("if (!_busy) DisposeLifetime();", self.dispose)
        self.assertIn("private void DisposeLifetime()", self.pane)
        self.assertIn("if (_disposed) DisposeLifetime();", self.pane)
        self.assertIn("private bool _lifetimeDisposed;", self.pane)
        self.assertLess(self.dispose.index("_lifetime.Cancel();"),
                        self.dispose.index("if (!_busy) DisposeLifetime();"))

    def test_inactive_tab_cannot_reclaim_focus_on_reattach(self):
        self.assertIn("if (!_disposed && !_suspended) Focus();", self.attached)
        self.assertIn("_suspended = false;", self.pane)
        self.assertIn("Focus();", self.pane.split("internal void ResumeInspection()", 1)[1].split(
            "internal async Task RefreshAsync()", 1)[0])

    def test_reopening_same_document_allocates_fresh_workspace_identity(self):
        self.assertIn("ReopenedOfficeDocumentGetsNewTabIdentityAndNoStaleSession",
                      self.model_tests)
        self.assertIn("Assert.AreNotEqual(first.Id, reopened.Id)", self.model_tests)
        self.assertIn("Assert.AreEqual(reopened.Id, workspace.ActiveId)", self.model_tests)
        self.assertIn("Assert.IsFalse(workspace.Tabs.Any(t => t.Id == first.Id))",
                      self.model_tests)

    def test_reopen_does_not_change_read_only_security_contract(self):
        for forbidden in ("File.Delete(", "File.Move(", "File.Write", "File.Read",
                          "Directory.Delete(", "CaptureAsync(", "Discard(",
                          "Restore(", "Process.Start(", "HttpClient("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.pane)
        self.assertEqual(1, self.pane.count("readonly Button "))
        self.assertIn("_store.InspectIncomplete(", self.pane)


if __name__ == "__main__":
    unittest.main()
