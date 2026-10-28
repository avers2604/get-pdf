"""GD-31J.8 source contract for one-way snapshot-only lab IPC.

These tests are staged for later execution together with the local Qt/CEF build.
"""
from pathlib import Path
import unittest

SOURCE = Path(__file__).resolve().parent / "cef_staged_open.cpp"


class CefLabIpcContractTests(unittest.TestCase):
    def test_only_bounded_explicit_session_can_enable_jsonl_ipc(self):
        source = SOURCE.read_text(encoding="utf-8")
        self.assertIn("--ipc-session", source)
        self.assertIn("QUuid::WithoutBraces", source)
        self.assertIn("getdoc-eurodesktop-ipc/1", source)
        self.assertIn('ipc["kind"] = "snapshot"', source)
        self.assertIn('ipc["sequence"] = 1', source)
        self.assertIn('ipc["sessionId"] = sessionId', source)

    def test_snapshot_is_diagnostic_and_cannot_claim_verified_save_or_pdf(self):
        source = SOURCE.read_text(encoding="utf-8")
        self.assertIn('receipt["documentVerified"] = false', source)
        self.assertIn('receipt["saveVerified"] = false', source)
        self.assertIn('receipt["pdfVerified"] = false', source)
        self.assertIn('receipt["printApproved"] = false', source)
        self.assertNotIn('editor->Apply(new CAscMenuEvent(ASC_MENU_EVENT_TYPE_CEF_SAVE))', source)
        self.assertNotIn("QPrinter", source)

    def test_diagnostic_stream_contains_no_work_file_data(self):
        source = SOURCE.read_text(encoding="utf-8")
        self.assertIn('ipc["receipt"] = receipt', source)
        self.assertNotIn('ipc["source"] = path', source)
        self.assertNotIn('ipc["documentPath"]', source)


if __name__ == "__main__":
    unittest.main()
