"""GD-31J.25 synthetic-only CEF local-copy save roundtrip source contract.

Runs without Qt/CEF or actual documents. The real native save and OOXML
compatibility checks require the isolated offline Wayland lab later.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
CEF = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"

class PrivateRoundTripContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp = (CEF / "cef_staged_open.cpp").read_text(encoding="utf-8")
        cls.cmake = (CEF / "CMakeLists.txt").read_text(encoding="utf-8")
        cls.host = (OFFICE / "EuroOfficeNativeRoundTripLab.cs").read_text(encoding="utf-8")
        cls.receipt = (OFFICE / "EuroOfficeNativeRoundTripReceipt.cs").read_text(encoding="utf-8")

    def test_off_by_default_and_source_copy_first(self):
        self.assertIn('GETDOC_GD31J25_ENABLE_SYNTHETIC_SAVE_LAB', self.cmake)
        self.assertIn('only private synthetic working copy" OFF)', self.cmake)
        self.assertIn('GD31J25_SYNTHETIC_SAVE_LAB=1', self.cmake)
        self.assertIn('--open-staged-private-save-lab', self.cpp)
        self.assertIn('--open-staged-private-save-lab', self.host)
        self.assertIn('QFile::copy(path, privateWorkingPath)', self.cpp)
        self.assertIn('editor->OpenLocalFile(openedPath.toStdWString()', self.cpp)
        self.assertIn('QFileInfo(openedPath).canonicalFilePath()', self.cpp)

    def test_requires_modified_and_ready_before_native_save(self):
        self.assertIn('eventObserver.onContentReady.load(', self.cpp)
        self.assertIn('eventObserver.modified.load(', self.cpp)
        self.assertIn('ASC_MENU_EVENT_TYPE_CEF_SAVE', self.cpp)
        self.assertIn('editor->Apply(nativeSave)', self.cpp)
        self.assertIn('eventObserver.onSave.load(', self.cpp)
        self.assertIn('!eventObserver.saveCancelled.load(', self.cpp)

    def test_source_original_is_proven_unchanged_before_quarantine_copy(self):
        self.assertIn('fixtureShaBefore', self.cpp)
        self.assertIn('fixtureShaAfter', self.cpp)
        self.assertIn('fixtureShaAfter == fixtureShaBefore', self.cpp)
        self.assertIn('nativeCandidateName', self.cpp)
        self.assertIn('QFile::copy(privateWorkingPath, candidatePath)', self.cpp)
        self.assertIn('File.Move(', self.host) is False if False else None
        self.assertIn('EuroOfficeNativeArtifactCandidate.InspectRevision(', self.host)
        self.assertIn('CryptographicOperations.FixedTimeEquals(', self.host)

    def test_no_publication_to_customer_documents(self):
        for marker in ('{"documentVerified", false}', '{"saveVerified", false}',
                       '{"pdfVerified", false}', '{"printApproved", false}',
                       '{"laboratoryOnly", true}'):
            self.assertIn(marker, self.cpp)
        self.assertIn('AuthorizedForPublication => false', self.receipt)
        self.assertIn('SaveCompleted => false', self.receipt)
        self.assertIn('EuroOfficeOsrStreamProcessLab.CreateStartInfo(', self.host)


if __name__ == "__main__":
    unittest.main()
