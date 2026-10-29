"""GD-31J.26 native Save As dialog interception: source-only offline gate.

Does not execute a CEF or build SDK. Tests only ensure guarded path+format
selection are wired, and no unrestricted user paths are ever accepted.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
LAB=ROOT/"probes/eurooffice-desktop/cef_lab"
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"

class NativeSaveAsContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp=(LAB/"cef_staged_open.cpp").read_text(encoding="utf-8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text(encoding="utf-8")
        cls.host=(OFFICE/"EuroOfficeNativeSaveAsLab.cs").read_text(encoding="utf-8")
        cls.receipt=(OFFICE/"EuroOfficeNativeSaveAsReceipt.cs").read_text(encoding="utf-8")

    def test_off_by_default_and_only_exact_synthetic_format(self):
        self.assertIn('GETDOC_GD31J26_ENABLE_GUARDED_SAVEAS_LAB',self.cmake)
        self.assertIn('only private same-format Save As path" OFF)',self.cmake)
        self.assertIn('GETDOC_GD31J26_GUARDED_SAVEAS_LAB=1',self.cmake)
        self.assertIn('GETDOC_GD31J25_ENABLE_SYNTHETIC_SAVE_LAB',self.cmake)
        self.assertIn('--open-staged-guarded-saveas-lab',self.cpp)
        self.assertIn('--open-staged-guarded-saveas-lab',self.host)
        self.assertIn('ValidateFormat(fixtureName, selectedFormat)',self.host)
        self.assertIn('QFileInfo(fixtureName).suffix()',self.cpp)

    def test_real_pinned_sdk_saveas_event_and_typed_response(self):
        self.assertIn('window.AscDesktopEditor.LocalFileSave',self.cpp)
        self.assertIn("'saveas=true'",self.cpp)
        self.assertIn('ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE',self.cpp)
        self.assertIn('ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE_PATH',self.cpp)
        self.assertIn('new NSEditorApi::CAscLocalSaveFileDialog()',self.cpp)
        self.assertIn('editor->Apply(saveAsReply)',self.cpp)
        self.assertIn('saveAsRequestSeen.load(',self.cpp)
        self.assertIn('event->get_SenderId() == expectedViewId.load(',self.cpp)

    def test_guarded_private_destination_and_no_overwrite(self):
        self.assertIn('QDir(profile.path()).filePath(',self.cpp)
        self.assertIn('"gd31j26-private-saveas."',self.cpp)
        self.assertIn('QFileInfo::exists(privateSaveAsPath)',self.cpp)
        self.assertIn('QFile::copy(privateSaveAsPath, candidatePath)',self.cpp)
        self.assertIn('fixtureShaAfter == fixtureShaBefore',self.cpp)
        self.assertIn('gd31j26-saveas-candidate-',self.cpp)
        for token in ('{"documentVerified", false}', '{"saveVerified", false}',
                      '{"pdfVerified", false}', '{"printApproved", false}',
                      '{"laboratoryOnly", true}'):
            self.assertIn(token,self.cpp)

    def test_post_native_ooXML_review_does_not_publish(self):
        self.assertIn('EuroOfficeNativeArtifactCandidate.InspectRevision(',self.host)
        self.assertIn('CryptographicOperations.FixedTimeEquals(',self.host)
        self.assertNotIn('File.Move(',self.host)
        self.assertNotIn('File.Delete(',self.host)
        self.assertIn('SaveCompleted => false',self.receipt)
        self.assertIn('AuthorizedForPublication => false',self.receipt)


if __name__=="__main__":
    unittest.main()
