"""GD-31J.4 source-level fail-closed contract checks.

These checks do not prove CEF works. The compiled GUI E2E check belongs on the
approved isolated Windows/Astra lab, never CI or an employee workstation.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parent
STAND = ROOT / "cef_empty_view.cpp"
BUILD = ROOT / "CMakeLists.txt"


class EmptyCefViewContractTests(unittest.TestCase):
    def test_empty_view_is_opt_in_and_separate_from_default_cef_init(self):
        cmake = BUILD.read_text(encoding="utf-8")
        self.assertIn('GETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB', cmake)
        self.assertIn('OFF)', cmake)
        self.assertIn('getdoc-eurooffice-cef-empty-view-lab', cmake)
        self.assertIn('cef_empty_view.cpp', cmake)

    def test_native_view_is_owned_by_qcefview_and_destroyed_by_manager(self):
        code = STAND.read_text(encoding="utf-8")
        self.assertIn('view->Create(&manager, cvwtEditor)', code)
        self.assertIn('manager.DestroyCefView(viewId)', code)
        self.assertIn('manager.CloseApplication()', code)
        self.assertIn('CApplicationCEF::Prepare', code)
        self.assertIn('cef.Init_CEF(&manager', code)
        self.assertNotIn('manager.CreateCefEditor(', code)

    def test_documents_and_printing_cannot_be_accessed(self):
        code = STAND.read_text(encoding="utf-8")
        for forbidden in (
            'OpenLocalFile(',
            'CreateLocalFile(',
            'SaveAs(',
            'Apply(new',
            'ExecuteCommand(',
            'GetFileFormat(',
            'QFileDialog',
            'QDesktopServices',
            'print-direct',
        ):
            self.assertNotIn(forbidden, code)
        self.assertIn('--cef-empty-view-lab', code)
        self.assertIn('QTemporaryDir', code)
        self.assertIn('QTimer::singleShot', code)

    def test_no_upstream_download_or_implicit_release_in_cmake(self):
        cmake = BUILD.read_text(encoding="utf-8")
        self.assertNotIn('FetchContent_Declare(', cmake)
        self.assertNotIn('ExternalProject_Add(', cmake)
        self.assertNotIn('file(DOWNLOAD ', cmake)
        self.assertIn('GETDOC_GD31J3_LOCAL_ISOLATION_CONFIRMED', cmake)


if __name__ == "__main__":
    unittest.main()
