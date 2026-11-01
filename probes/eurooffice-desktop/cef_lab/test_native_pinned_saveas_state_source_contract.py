"""GD-31J.27e: source-only integration contract for pinned v2 lifecycle guards.

Actual C++ tests/Qt CEF runs remain manual and are not claimed as passed.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class V2StateGuardSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = (LAB / "cef_staged_open.cpp").read_text("utf-8")
        cls.parser = (LAB / "native_pinned_saveas_event_v2.py").read_text("utf-8")
        cls.dotnet = (OFFICE / "EuroOfficePinnedNativeSaveAsEventV2.cs").read_text("utf-8")
        cls.supervisor = (LAB / "native_pinned_v2_supervisor.py").read_text("utf-8")

    def test_only_v2_has_ready_arming_and_terminal_abort(self):
        for term in (
            "#ifdef GETDOC_GD31J27C_NATIVE_EVENT_V2_LAB",
            "v2DialogArmed{false}",
            "v2TerminalAbort{false}",
            "v2TimedOut{false}",
            "v2DialogArmed.exchange(",
            "v2TerminalAbort.store(true",
            "v2DialogArmed.store(true",
            "eventObserver.saveDialogCount.load(std::memory_order_acquire) != 1",
            "eventObserver.saveOnSaveCount.load(std::memory_order_acquire) != 0",
            "eventObserver.v2TerminalAbort.load(std::memory_order_acquire)",
            "eventObserver.onContentReady.load(std::memory_order_acquire)",
            "bindingSeen && conversionObserved",
        ):
            self.assertIn(term, self.native)
        self.assertIn('"--open-staged-native-saveas-event-v2-lab"', self.native)
        self.assertIn('"--open-staged-native-saveas-lab"', self.native)

    def test_duplicate_on_save_is_distinct_from_timeout_and_candidate_missing(self):
        for term in (
            'saves > 1 ? "duplicate_on_save"',
            'saveLabTimedOut ? "timeout" : "candidate_missing"',
            '&& !saveLabTimedOut;',
            'eventObserver.v2TimedOut.store(true, std::memory_order_release)',
        ):
            self.assertIn(term, self.native)
        self.assertIn('expected_code = "duplicate_on_save"', self.parser)
        self.assertIn('"candidate_missing") and', self.parser)
        self.assertIn('"duplicate_on_save"', self.dotnet)
        self.assertIn('expectedCode == "candidate_missing"', self.dotnet)

    def test_existing_v1_and_runtime_pin_supervision_are_not_bypassed(self):
        self.assertIn('{"schema", "getdoc-native-saveas-candidate-lab/1"}', self.native)
        self.assertIn('{"schema", "getdoc-native-saveas-pinned-event-lab/2"}', self.native)
        self.assertIn('if (eventV2Lab)', self.native)
        self.assertIn('runtimeLibraryPinsMatched', self.supervisor)
        self.assertIn('joined_v2.from_stream(', self.supervisor)
        for block in (self.native, self.parser, self.dotnet):
            self.assertIn("approvedForProduction", block)
        self.assertIn('"approvedForProduction": False', self.parser)
        self.assertIn("public bool ApprovedForProduction => false;", self.dotnet)


if __name__ == "__main__":
    unittest.main()
