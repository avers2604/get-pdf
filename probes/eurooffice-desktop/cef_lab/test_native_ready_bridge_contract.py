"""GD-31J.24 static-only contract for a manually patched pinned CEF SDK.

NO builds, network, Qt, CEF, real documents, WORM or save/print rights.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / "probes/eurooffice-desktop/cef_lab"
SRC = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class NativeReadyBridgeContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp = (LAB / "cef_staged_open.cpp").read_text(encoding="utf-8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text(encoding="utf-8")
        cls.overlay = (LAB / "ready_bridge_overlay.py").read_text(encoding="utf-8")
        cls.lab = (SRC / "EuroOfficeNativeReadyProbeLab.cs").read_text(encoding="utf-8")
        cls.receipt = (SRC / "EuroOfficeNativeReadyCandidate.cs").read_text(encoding="utf-8")

    def test_off_by_default_sdk_overlay_and_runtime_binary_pin_gate(self):
        self.assertIn('GETDOC_GD31J24_ENABLE_READY_EVENT_LAB', self.cmake)
        self.assertIn('renderer content-ready notification only" OFF)', self.cmake)
        self.assertIn('ready_bridge_overlay.py', self.cmake)
        self.assertIn('--verify', self.cmake)
        self.assertIn('APPROVED_ASC_DOCUMENTSCORE_SHA256', self.cmake)
        self.assertIn('APPROVED_ASC_QT_SCORE_SHA256', self.cmake)
        self.assertIn('GETDOC_GD31J24_READY_EVENT_LAB=1', self.cmake)
        self.assertIn('GETDOC_GD31J23_ENABLE_CONVERSION_EVIDENCE_LAB', self.cmake)

    def test_real_sdk_listener_event_and_id_filter_are_required(self):
        self.assertIn('GETDOC_GD31J24_READY_EVENT_BRIDGE', self.cpp)
        self.assertIn('ASC_MENU_EVENT_TYPE_GETDOC_DOCUMENT_CONTENT_READY', self.cpp)
        self.assertIn('event->get_SenderId() == expectedViewId.load(', self.cpp)
        self.assertIn('onContentReady.store(true', self.cpp)
        self.assertIn('conversionObserved', self.cpp)
        self.assertIn('eventObserver.onContentReady.load(', self.cpp)
        self.assertIn('--open-staged-ready-event-lab', self.cpp)
        self.assertIn('--open-staged-ready-event-lab', self.lab)

    def test_ready_event_never_promotes_real_document_privileges(self):
        for item in ('{"documentVerified", false}', '{"saveVerified", false}',
                     '{"pdfVerified", false}', '{"printApproved", false}',
                     '{"laboratoryOnly", true}'):
            self.assertIn(item, self.cpp)
        for item in ('DocumentVerified => false', 'EditReady => false',
                     'SaveCompleted => false', 'ApprovedForProduction => false',
                     'ReadyAndConversionCorrelated'):
            self.assertIn(item, self.receipt)
        self.assertIn('EuroOfficeNativeBindingProbeLab.RunRawLabAsync', self.lab)

    def test_source_overlay_does_not_claim_on_document_content_ready_is_trusted(self):
        self.assertIn('on_document_content_ready', self.overlay)
        self.assertIn('pListener->OnEvent(pReady)', self.overlay)
        self.assertIn('Never auto-applies', self.overlay)
        self.assertIn('Unapproved checkout', self.overlay)


if __name__ == "__main__":
    unittest.main()
