"""GD-31J.27c source contracts (deferred): optional pinned event-v2 lab."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class PinnedNativeEventV2SourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp = (LAB / "cef_staged_open.cpp").read_text("utf-8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text("utf-8")
        cls.reader = (LAB / "native_pinned_saveas_event_v2.py").read_text("utf-8")

    def test_new_v2_cli_is_opt_in_and_legacy_v1_command_still_exists(self):
        self.assertIn("--open-staged-native-saveas-event-v2-lab", self.cpp)
        self.assertIn('--open-staged-native-saveas-lab', self.cpp)
        self.assertIn("GETDOC_GD31J27C_NATIVE_EVENT_V2_LAB", self.cpp)
        self.assertIn("GETDOC_GD31J27C_ENABLE_NATIVE_EVENT_V2_LAB", self.cmake)
        self.assertIn("GETDOC_GD31J27C_EVENT_V2_ISOLATION_CONFIRMED", self.cmake)
        self.assertIn('{"schema", "getdoc-native-saveas-candidate-lab/1"}', self.cpp)
        self.assertIn('{"schema", "getdoc-native-saveas-pinned-event-lab/2"}', self.cpp)
        self.assertIn('{"receipt", evidence}', self.cpp)

    def test_bounded_native_event_sequence_and_fail_closed_publish(self):
        for term in (
            "saveEventSerial.fetch_add(1", "firstDialogSequence",
            "firstOnSaveSequence", "saveDialogCount", "saveOnSaveCount",
            "invalidDialogObserved", "unexpectedOnSaveObserved",
            "eventV2Lab", "v2EventChainReady",
            "eventObserver.saveRequestCount.load(",
            "Gd31J26::NativeSaveAsPublish(",
        ):
            self.assertIn(term, self.cpp)

    def test_no_production_privileges_and_read_only_v2_parser(self):
        self.assertIn('{"approvedForProduction", false}', self.cpp)
        self.assertIn('{"nativeSaveProven", false}', self.cpp)
        self.assertIn('"approvedForProduction": False', self.reader)
        self.assertIn('object_pairs_hook=_distinct', self.reader)
        self.assertNotIn('subprocess', self.reader)
        self.assertNotIn('write_bytes(', self.reader)
        self.assertNotIn('os.replace(', self.reader)


if __name__ == "__main__":
    unittest.main()
