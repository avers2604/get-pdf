"""Offline source contract for GD-31J.28 native SaveAs v2 failure evidence.

Only checks code presence/config: NOT a native runtime or Qt/CEF integration test.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
LAB=ROOT/"probes/eurooffice-desktop/cef_lab"
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"

class NativeSaveAsV2SourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp=(LAB/"cef_staged_open.cpp").read_text(encoding="utf-8")
        cls.host=(OFFICE/"EuroOfficeNativeSaveAsLab.cs").read_text(encoding="utf-8")
        cls.parser=(OFFICE/"EuroOfficeNativeSaveAsEventReceipt.cs").read_text(encoding="utf-8")
        cls.lifecycle=(OFFICE/"EuroOfficeNativeSaveAsLifecycle.cs").read_text(encoding="utf-8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text(encoding="utf-8")

    def test_versioned_nested_untrusted_v1_plus_v2_without_paths(self):
        self.assertIn('getdoc-native-saveas-event-lab/2',self.cpp)
        self.assertIn('{"failureCode", failureCode}',self.cpp)
        self.assertIn('{"phase", nativePhase}',self.cpp)
        self.assertIn('{"dialogCount",',self.cpp)
        self.assertIn('{"onSaveCount",',self.cpp)
        self.assertIn('{"dialogSequence",',self.cpp)
        self.assertIn('{"onSaveSequence",',self.cpp)
        self.assertIn('{"receipt", evidence}',self.cpp)
        self.assertIn('EuroOfficeNativeSaveAsEventReceipt.Parse(',self.host)
        self.assertNotIn('EuroOfficeNativeSaveAsReceipt.Parse(\n            raw',self.host)
        self.assertIn('Core',self.parser)

    def test_callback_counters_and_order_gate_before_candidate(self):
        self.assertIn('saveAsEventSerial.fetch_add(',self.cpp)
        self.assertIn('firstDialogSequence.store(',self.cpp)
        self.assertIn('firstOnSaveSequence.store(',self.cpp)
        self.assertIn('onSaveCount.fetch_add(',self.cpp)
        self.assertIn('saveAsRequestCount.load(std::memory_order_acquire) == 1',self.cpp)
        self.assertIn('onSaveCount.load(std::memory_order_acquire) == 1',self.cpp)
        self.assertIn('firstOnSaveSequence.load(std::memory_order_acquire) >',self.cpp)
        self.assertIn('firstDialogSequence.load(std::memory_order_acquire)',self.cpp)
        self.assertIn('saveAsLifecycleAborted',self.cpp)

    def test_no_production_saving_and_manual_e2e_gate(self):
        self.assertIn('AuthorizedForPublication => false',self.parser)
        self.assertIn('SaveCompleted => false',self.parser)
        self.assertIn('GETDOC_GD31J26_ENABLE_GUARDED_SAVEAS_LAB',self.cmake)
        self.assertIn('EuroOfficeNativeSaveAsLifecycle.Classify(',self.host)
        self.assertIn('NativeEvidence',self.host)
        self.assertIn('native failureCode',self.lifecycle)


if __name__ == "__main__":
    unittest.main()
