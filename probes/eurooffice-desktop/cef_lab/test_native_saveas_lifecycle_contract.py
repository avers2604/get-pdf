"""GD-31J.27: source-level regressions for native Save As dispatch and lifecycle.

Offline static source checks only; not real Qt/CEF builds or runtime tests.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
LAB=ROOT/"probes/eurooffice-desktop/cef_lab"
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"

class SaveAsLifecycleContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp=(LAB/"cef_staged_open.cpp").read_text(encoding="utf-8")
        cls.lab=(OFFICE/"EuroOfficeNativeSaveAsLab.cs").read_text(encoding="utf-8")
        cls.lifecycle=(OFFICE/"EuroOfficeNativeSaveAsLifecycle.cs").read_text(encoding="utf-8")

    def test_seven_argument_save_as_routes_through_all_required_cef_gates(self):
        self.assertIn('const bool saveAsLab = argc == 7 &&',self.cpp)
        self.assertIn('|| saveAsLab;',self.cpp)
        self.assertIn('const bool bindingLab =',self.cpp)
        self.assertIn('const bool conversionLab =',self.cpp)
        self.assertIn('if (!ordinaryLab && !osrLab && !streamLab && !bindingLab)',self.cpp)
        self.assertIn('if (saveAsLab && (!bindingLab || !conversionLab || !readyLab)) return 2;',self.cpp)
        self.assertIn('if (saveAsLab && app.platformName() != "wayland") return 9;',self.cpp)

    def test_native_save_as_denies_duplicate_and_late_callbacks(self):
        self.assertIn('saveAsRequestCount.load(',self.cpp)
        self.assertIn('saveAsTargetAssigned',self.cpp)
        self.assertIn('!eventObserver.rejectedSaveAsRequest.load(',self.cpp)
        self.assertIn('eventObserver.acceptSaveAsDialog.store(false',self.cpp)
        self.assertIn('saveAsLifecycleAborted',self.cpp)
        self.assertIn('saveAsLifetimeTimeout',self.cpp)
        self.assertIn('saveAsTargetConflict',self.cpp)
        self.assertIn('!saveAsLifecycleAborted',self.cpp)
        self.assertIn('if (!saveAsTriggered && !editor->IsSaveLocked())', self.cpp)
        # Reply is allowed AFTER the initial SaveAs request even when the
        # SDK's converter reports a transient save lock.
        self.assertIn('if (saveAsTriggered && !saveAsTargetAssigned', self.cpp)

    def test_saveas_completion_never_grants_document_save_or_publication(self):
        self.assertIn('EuroOfficeNativeSaveAsLifecycle.Classify(',self.lab)
        self.assertIn('CandidateValidatedAsOoxml',self.lab)
        self.assertIn('SaveCompleted => false',self.lab)
        self.assertIn('AuthorizedForPublication => false',self.lab)
        self.assertIn('SavedCandidateValidatedButUntrusted',self.lifecycle)
        self.assertIn('DuplicateDialog',self.lifecycle)
        self.assertIn('SaveCancelled',self.lifecycle)
        self.assertIn('TargetConflict',self.lifecycle)
        self.assertIn('SaveNotAcknowledged',self.lifecycle)


if __name__=="__main__":
    unittest.main()
