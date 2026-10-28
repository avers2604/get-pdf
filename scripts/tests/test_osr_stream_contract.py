"""GD-31J.13 deferred local-only source safety contract.

Never starts the CEF process or reads actual corporate documents.
The full runtime and cross-platform UI acceptance remain deferred.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
NATIVE = ROOT / "probes/eurooffice-desktop/cef_lab/cef_staged_open.cpp"
CMAKE = ROOT / "probes/eurooffice-desktop/cef_lab/CMakeLists.txt"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"
DESKTOP = ROOT / "src/GetDocument.Desktop"


class CefWaylandStreamContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = NATIVE.read_text("utf-8")
        cls.cmake = CMAKE.read_text("utf-8")
        cls.reader = (OFFICE / "EuroOfficeOsrStreamFrame.cs").read_text("utf-8")
        cls.process = (OFFICE / "EuroOfficeOsrStreamProcessLab.cs").read_text("utf-8")
        cls.main = (DESKTOP / "MainWindow.cs").read_text("utf-8")
        cls.pane = (DESKTOP / "Office/NativeOfficeDiagnosticPane.cs").read_text("utf-8")
        cls.sink = (DESKTOP / "Office/AvaloniaNativeOsrLabFrameSink.cs").read_text("utf-8")

    def test_osr_stream_is_explicitly_disabled_by_default(self):
        self.assertIn("GETDOC_GD31J13_ENABLE_OSR_STREAM_LAB", self.cmake)
        self.assertIn('streaming on isolated Wayland VM" OFF)', self.cmake)
        self.assertIn("GETDOC_GD31J13_OSR_STREAM_LAB=1", self.cmake)
        self.assertIn("#ifndef GETDOC_GD31J13_OSR_STREAM_LAB", self.native)
        self.assertIn("--open-staged-wayland-stream-lab", self.native)
        self.assertIn('qgetenv("QT_QPA_PLATFORM")', self.native)
        self.assertIn('app.platformName()', self.native)

    def test_real_cef_onpaint_nonblocking_no_unbounded_buffer(self):
        for needle in ("QCefView::OnPaint(buffer, width, height);",
                       "m_latestFrame = preview;", "m_latestRevision",
                       "O_NONBLOCK", "EAGAIN", "streamOutput.HasPending()",
                       "framesProduced >= 8", "frameSampleTimer.start(500)",
                       "streamOutput.FlushFor(1500)", "cef.DoMessageLoopEvent()"):
            self.assertIn(needle, self.native)
        self.assertIn('"getdoc-cef-osr-stream/1"', self.native)
        self.assertIn('"cef-osr-wayland-synthetic-only"', self.native)
        self.assertIn('"time-limit"', self.native)

    def test_reader_is_session_bound_and_never_verifies_document_or_print(self):
        for needle in ('MaximumFrames = 8', 'MaximumPngBytes = 48 * 1024',
                       'session.ToString("N")', 'tab.ToString("N")',
                       'SHA256.HashData(bytes)', 'EndFields', 'ReadOneLineAsync',
                       'ApprovedForProduction => false',
                       'SaveCompleted => false', 'PrintApproved => false'):
            self.assertIn(needle, self.reader)
        self.assertIn("host.VerifyBinary()", self.process)
        self.assertIn('RedirectStandardOutput = true', self.process)
        self.assertIn('ValidateFixtureName(fixtureName)', self.process)
        self.assertIn('Process.Kill', self.process) if False else None
        self.assertIn('process.Kill(entireProcessTree: true)', self.process)

    def test_avalonia_lab_frame_never_becomes_production_editor(self):
        self.assertIn("internal async Task StartNativeCefOsrLabStreamAsync(", self.main)
        self.assertIn('if (!labApproved)', self.main)
        self.assertIn("_nativeOsrLabStreams", self.main)
        self.assertIn("_nativeOsrLabTabs", self.main)
        self.assertIn("ShowNativeOsrLabFrame(", self.main)
        self.assertIn("pane.SetCefOsrLabFrame(frame)", self.main)
        self.assertIn("Dispatcher.UIThread.Post(", self.sink)
        self.assertIn("SetCefOsrLabFrame(EuroOfficeOsrStreamFrame frame)", self.pane)
        self.assertIn("документ и сохранение не подтверждены", self.pane)


if __name__ == "__main__":
    unittest.main()
