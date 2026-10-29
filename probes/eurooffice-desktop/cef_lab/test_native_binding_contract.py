"""GD-31J.22 static guard: pinned EuroOffice SDK native path-binding only.

Source checks only. They do NOT run Qt/CEF nor certify OOXML import, OS sandbox
or real editing. The executable stays isolated and default-OFF.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
CEF = ROOT / "probes/eurooffice-desktop/cef_lab"
DESKTOP = ROOT / "src/GetDocument.Desktop"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class NativeBindingSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = (CEF / "cef_staged_open.cpp").read_text(encoding="utf-8")
        cls.cmake = (CEF / "CMakeLists.txt").read_text(encoding="utf-8")
        cls.host = (OFFICE / "EuroOfficeNativeBindingProbeLab.cs").read_text(encoding="utf-8")
        cls.receipt = (OFFICE / "EuroOfficeNativeBindingReceipt.cs").read_text(encoding="utf-8")
        cls.window = (DESKTOP / "MainWindow.cs").read_text(encoding="utf-8")
        cls.pane = (DESKTOP / "Office/NativeOfficeDiagnosticPane.cs").read_text(encoding="utf-8")

    def test_default_off_and_synthetic_only(self):
        self.assertIn('GETDOC_GD31J22_ENABLE_BINDING_LAB', self.cmake)
        self.assertIn('binding via pinned SDK, no production rights" OFF)', self.cmake)
        self.assertIn('GETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB', self.cmake)
        self.assertIn('GETDOC_GD31J22_BINDING_LAB=1', self.cmake)
        self.assertIn('--open-staged-binding-lab', self.native)
        self.assertIn('--open-staged-binding-lab', self.host)
        self.assertIn('VerifySyntheticFixture(staging, fixtureName, path, reason)', self.native)
        self.assertIn('EuroOfficeOsrStreamProcessLab.CreateStartInfo(', self.host)

    def test_real_sdk_binding_polled_on_qt_thread_not_mouse_or_onpaint(self):
        self.assertIn('editor->OpenLocalFile(path.toStdWString(), format, L"")', self.native)
        self.assertIn('editor->GetLocalFilePath()', self.native)
        self.assertIn('bindingPollTimer.start(250)', self.native)
        self.assertIn('cef.DoMessageLoopEvent()', self.native)
        self.assertIn('osrLab || streamLab || bindingLab', self.native)
        self.assertIn('QFileInfo(reported).canonicalFilePath()', self.native)
        self.assertNotIn('GetLocalFilePath()', self.host)

    def test_native_telemetry_is_untrusted_and_no_editor_capabilities(self):
        for flag in ('{"documentVerified", false}', '{"saveVerified", false}',
                     '{"pdfVerified", false}', '{"printApproved", false}',
                     '{"laboratoryOnly", true}'):
            self.assertIn(flag, self.native)
        self.assertIn('bindingSeen && viewCallback', self.native)
        self.assertIn('ApprovedForProduction => false', self.receipt)
        self.assertIn('SaveCompleted => false', self.receipt)
        self.assertIn('ExpectedFields', self.receipt)
        self.assertIn('fields.Add(property.Name)', self.receipt)
        self.assertIn('if (observed && !(cef && path))', self.receipt)

    def test_diagnostic_tab_keeps_user_launcher_unmodified(self):
        self.assertIn('internal async Task StartNativeBindingLabAsync(', self.window)
        self.assertIn('ReportNativeBindingEvidence(', self.window)
        self.assertIn('SetNativeBindingEvidence(', self.pane)
        self.assertNotIn('OpenFromLaunch(', self.host)
        self.assertNotIn('PreviewFactory.Create(', self.host)


if __name__ == "__main__":
    unittest.main()
