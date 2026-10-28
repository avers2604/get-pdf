"""Source-contract regression coverage for GD-31J.7 native SDK event forwarding.

These tests are intentionally NOT executed in this development phase, per
project's deferred local-laboratory policy.
"""
from pathlib import Path
import unittest

CPP = Path(__file__).resolve().parent / "cef_staged_open.cpp"


class NativeEventAdapterContractTests(unittest.TestCase):
    def test_trusted_sdk_event_listener_is_attached_and_removed(self):
        code = CPP.read_text(encoding="utf-8")
        self.assertIn("CAscCefMenuEventListener", code)
        self.assertIn("manager.SetEventListener(&eventObserver)", code)
        self.assertIn("manager.SetEventListener(nullptr)", code)
        self.assertIn("NSEditorApi::CAscCefMenuEventListener::OnEvent(event)", code)

    def test_events_are_notifications_not_successful_file_operations(self):
        code = CPP.read_text(encoding="utf-8")
        for event in (
            "ASC_MENU_EVENT_TYPE_CEF_MODIFY_CHANGED",
            "ASC_MENU_EVENT_TYPE_CEF_ONSAVE",
            "ASC_MENU_EVENT_TYPE_CEF_PRINT_END",
        ):
            self.assertIn(event, code)
        self.assertIn("nativeEvents", code)
        self.assertIn('"saveVerified"', code)
        self.assertIn('"pdfVerified"', code)
        self.assertNotIn("ASC_MENU_EVENT_TYPE_CEF_SAVEFILEDIALOG", code)
        self.assertNotIn("ASC_MENU_EVENT_TYPE_CEF_SAVE)", code)
        self.assertNotIn("ASC_MENU_EVENT_TYPE_CEF_PRINT_START", code)

    def test_never_prints_file_name_or_contents_in_event_receipt(self):
        code = CPP.read_text(encoding="utf-8")
        self.assertNotIn("event->m_pData->get_FilePath", code)
        self.assertIn("GD31J7_EVENTS_ONLY", code)


if __name__ == "__main__":
    unittest.main()
