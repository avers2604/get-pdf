"""GD-31J.26 source-only native SaveAs lab contract; no Qt/CEF execution."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class NativeSaveAsContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp = (LAB / "cef_staged_open.cpp").read_text(encoding="utf-8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text(encoding="utf-8")
        cls.policy = (LAB / "native_saveas_policy.h").read_text(encoding="utf-8")
        cls.csharp = (OFFICE / "EuroOfficeNativeSaveAsCandidate.cs").read_text(encoding="utf-8")

    def test_opt_in_requires_exact_ready_lab_and_does_not_join_installer(self):
        self.assertIn('GETDOC_GD31J26_ENABLE_NATIVE_SAVEAS_LAB', self.cmake)
        self.assertIn('synthetic SaveAs via exact vendor event" OFF)', self.cmake)
        self.assertIn('GETDOC_GD31J24_ENABLE_READY_EVENT_LAB', self.cmake)
        self.assertIn('GETDOC_GD31J26_NATIVE_SAVEAS_LAB=1', self.cmake)
        self.assertIn('--open-staged-native-saveas-lab', self.cpp)
        self.assertIn('native_saveas_policy.h', self.cpp)

    def test_synthetic_shadow_is_opened_not_original_fixture(self):
        self.assertIn('PrepareSyntheticShadow(', self.cpp)
        self.assertIn('editor->OpenLocalFile(path.toStdWString()', self.cpp)
        self.assertIn('LabSourceSha256(', self.policy)
        self.assertIn('QFile::copy(sourcePath, shadowPath)', self.policy)
        self.assertIn('gd31j6-fixture-manifest.json', self.cpp)
        self.assertIn('gd31j25-output', self.policy)

    def test_vendor_saveas_request_and_response_require_strict_id_and_gate(self):
        self.assertIn('ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE', self.cpp)
        self.assertIn('ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE_PATH', self.cpp)
        self.assertIn('event->get_SenderId() == expectedViewId.load(', self.cpp)
        self.assertIn('eventObserver.onContentReady.load(', self.cpp)
        self.assertIn('eventObserver.modified.load(', self.cpp)
        self.assertIn('eventObserver.savePathRequested.exchange(', self.cpp)
        self.assertIn('editor->Apply(reply)', self.cpp)
        self.assertIn('saveAnswerSent.store(true', self.cpp)
        self.assertIn('eventObserver.nativeOnSaveAfterAnswer', self.cpp)

    def test_no_overwrite_and_no_original_writes_or_production_grants(self):
        self.assertIn('::link(', self.policy)
        self.assertIn('QFileInfo::exists(finalPath)', self.policy)
        self.assertIn('NativeSaveAsPublish(', self.cpp)
        self.assertIn('nativeSaveProven', self.cpp)
        self.assertIn('{"saveVerified", false}', self.cpp)
        self.assertIn('{"approvedForProduction", false}', self.cpp)
        self.assertIn('NativeSaveProven => false', self.csharp)
        self.assertIn('ApprovedForProduction => false', self.csharp)
        self.assertNotIn('ExecuteInAllFrames(', self.cpp)
        self.assertNotIn('SaveAs(', self.cpp)

    def test_needs_native_event_and_published_bytes_before_any_candidate(self):
        self.assertIn('nativeOnSaveAfterAnswer.load(', self.cpp)
        self.assertIn('saveCancelledAfterAnswer.load(', self.cpp)
        self.assertIn('sourceUnchanged', self.cpp)
        self.assertIn('publishedToDisposableOutput', self.cpp)
        self.assertIn('nativeSaveRequestObserved', self.csharp)


if __name__ == "__main__":
    unittest.main()
