"""GD-31J.11 non-executing source contracts for cross-platform diagnostic raster.

This only checks source invariants. It does NOT prove Qt/CEF OSR, real editor
rendering, physical monitors, or safe image decoding on Windows/Astra.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"
DESKTOP = ROOT / "src/GetDocument.Desktop"
QT = ROOT / "probes/eurooffice-desktop/native_host/ipc_probe.cpp"


class NativeFrameShellContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.qt = QT.read_text(encoding="utf-8")
        cls.ipc = (OFFICE / "EuroOfficeNativeTabIpc.cs").read_text("utf-8")
        cls.frame = (OFFICE / "EuroOfficeNativeDiagnosticFrame.cs").read_text("utf-8")
        cls.pane = (DESKTOP / "Office/NativeOfficeDiagnosticPane.cs").read_text("utf-8")
        cls.main = (DESKTOP / "MainWindow.cs").read_text("utf-8")
        cls.coordinator = (OFFICE / "EuroOfficeNativeSessionCoordinator.cs").read_text("utf-8")

    def test_qt_frame_is_original_raster_not_native_editor_or_os_window(self):
        self.assertIn('command == "request-frame"', self.qt)
        self.assertIn('QImage frame(320, 180', self.qt)
        self.assertIn('"qt-painter-test-only"', self.qt)
        for forbidden in ('OpenLocalFile(', 'CreateCefEditor(', 'QWidget*',
                          'GetNativeWindowHandle(', 'HWND', 'xcb_reparent_window',
                          'print-direct'):
            self.assertNotIn(forbidden, self.qt)

    def test_bounded_frame_parser_checks_session_sequence_digest_and_png(self):
        for must_have in ('MaximumReplyCharacters', 'MaximumPngBytes',
                          'SHA256.HashData(png)', 'sessionId.ToString("N")',
                          'tabId.ToString("N")', 'GetInt32() != sequence',
                          '"IHDR"u8', 'RevisionCommitted => false',
                          'OfficeDocumentVerified => false',
                          'PrintApproved => false'):
            self.assertIn(must_have, self.frame)
        self.assertIn('RequestPreviewFrameAsync(', self.ipc)
        self.assertIn('EuroOfficeNativeDiagnosticFrame.Parse(', self.ipc)

    def test_frame_crosses_only_dispatcher_and_existing_diagnostic_tab(self):
        self.assertIn('RefreshDiagnosticFrameAsync(', self.coordinator)
        self.assertIn('frame.SessionId != sessionId', self.coordinator)
        self.assertIn('ShowNativeDiagnosticFrame(', self.main)
        self.assertIn('pane.SetDiagnosticFrame(frame)', self.main)
        self.assertIn('SetDiagnosticFrame(EuroOfficeNativeDiagnosticFrame frame)', self.pane)
        self.assertIn('new Bitmap(input)', self.pane)
        self.assertIn('Qt IPC', self.pane)
        self.assertIn('не подтвержден', self.main.lower())


if __name__ == "__main__":
    unittest.main()
