"""GD-31J.10 source constraints for deferred local-only engineering acceptance.

These are *not* UI, .NET, Qt/CEF or subprocess tests.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
DESKTOP = ROOT / "src/GetDocument.Desktop"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class NativeSessionDispatchContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dispatch = (DESKTOP / "Office/AvaloniaNativeSessionView.cs").read_text("utf-8")
        cls.main = (DESKTOP / "MainWindow.cs").read_text("utf-8")
        cls.core = (OFFICE / "EuroOfficeNativeSessionCoordinator.cs").read_text("utf-8")
        cls.process = (OFFICE / "EuroOfficeNativeIpcProcessSession.cs").read_text("utf-8")

    def test_only_ui_dispatcher_may_update_native_status_pane(self):
        self.assertIn("Dispatcher.UIThread.CheckAccess()", self.dispatch)
        self.assertIn("Dispatcher.UIThread.Post(", self.dispatch)
        self.assertIn("window.ReportNativeDiagnosticCrash", self.dispatch)
        self.assertIn("window.ReportNativeDiagnosticConnected", self.dispatch)
        self.assertIn("window.CompleteNativeDiagnosticClose", self.dispatch)

    def test_close_process_precedes_removal_of_tab(self):
        code = self.core
        self.assertLess(code.index("await entry.Transport.CloseAsync(cancellationToken)"),
                        code.index("await _view.RemoveAsync(tabId, sessionId, CancellationToken.None)"))
        self.assertIn("await DisposeTransportAsync(entry)", code)
        self.assertIn("HasUnverifiedChangesAsync(", code)
        self.assertIn("confirmedDiscard", code)
        self.assertIn("StopMonitoring.Cancel()", code)

    def test_session_loss_notifies_ui_without_restart_or_document_rights(self):
        self.assertIn("WaitForExitAsync(", self.core)
        self.assertIn("ObserveHostExitAsync(", self.core)
        self.assertIn("current.SessionId != sessionId", self.core)
        self.assertIn("HostLostAsync(tabId, entry.SessionId", self.core)
        self.assertIn("AuthorizeDocumentOperations()", self.core)
        for forbidden in ("OpenLocalFile(", "CreateCefEditor(", "PublishRevisionToGup2",
                          "TransferPdfToGetPrintWizard", "QWindow", "SetParent("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.core)

    def test_integration_is_internal_only_not_production_autostart(self):
        self.assertIn("internal async Task StartNativeLabSessionAsync", self.main)
        self.assertIn("if (!labApproved)", self.main)
        self.assertIn("policy, sessionId, tabId, labApproved", self.main)
        self.assertIn("await coordinator.CloseAsync(tabId, snapshot.SessionId", self.main)
        self.assertIn("public Task WaitForExitAsync(CancellationToken cancellationToken)", self.process)


if __name__ == "__main__":
    unittest.main()
