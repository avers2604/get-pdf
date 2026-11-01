"""GD-31J.27f source contracts: bounded C++ callback telemetry, stable teardown,
and explicit manual arming. Deferred test execution by project agreement.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / "probes/eurooffice-desktop/cef_lab"


class BoundedNativeSaveAsLifecycleContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = (LAB / "cef_staged_open.cpp").read_text("utf-8")
        cls.header = (LAB / "native_saveas_event_budget.h").read_text("utf-8")
        cls.v2 = (LAB / "native_pinned_saveas_event_v2.py").read_text("utf-8")

    def test_atomic_event_budget_saturates_instead_of_wrapping(self):
        for token in (
            "namespace Gd31J27F", "TickBounded(", "compare_exchange_weak",
            "MAX_DIALOG_EVENTS = 64", "MAX_SAVE_EVENTS = 64",
            "MAX_SERIAL_EVENTS = 128", "overflow.store(true",
        ):
            self.assertIn(token, self.header)
        self.assertIn('#include "native_saveas_event_budget.h"', self.native)
        self.assertIn("Gd31J27F::TickBounded(", self.native)
        self.assertIn("v2EventBudgetOverflow", self.native)
        self.assertIn("!eventObserver.v2EventBudgetOverflow.load(", self.native)
        self.assertNotIn("saveEventSerial.fetch_add(", self.native)
        self.assertNotIn("saveDialogCount.fetch_add(", self.native)
        self.assertNotIn("saveOnSaveCount.fetch_add(", self.native)

    def test_operator_must_opt_in_after_ready_and_one_saveas_window(self):
        for token in (
            '#include <QPushButton>',
            "v2OperatorApproved{false}",
            "v2ArmButton",
            "QPushButton::clicked",
            "v2OperatorApproved.store(true",
            "v2OperatorApproved.load(std::memory_order_acquire)",
            "v2DialogArmed.store(true",
            "v2ArmButton->setEnabled(",
        ):
            self.assertIn(token, self.native)
        self.assertIn("GD31J27F_OPERATOR_LAB_ONLY", self.native)

    def test_owner_thread_settle_and_shutdown_detach_before_destroy(self):
        for token in (
            "nativeSaveSettleTimer", "nativeSaveSettleClock",
            "nativeSaveSettleClock.restart()", "app.quit()",
            "v2CallbacksInFlight", "v2CallbacksQuiescent",
            "nativeEventV2Enabled.store(false",
            "nativeSaveLabEnabled.store(false",
            "manager.SetEventListener(nullptr)",
            "manager.DestroyCefView(",
            "GD31J27F_REFUSED_UNQUIESCED_CALLBACKS",
        ):
            self.assertIn(token, self.native)
        self.assertLess(self.native.index("manager.SetEventListener(nullptr)"),
                        self.native.index("manager.DestroyCefView("))
        self.assertIn("static SyntheticEventObserver eventObserver;", self.native)

    def test_v1_native_mode_and_v2_parser_privilege_claims_unchanged(self):
        self.assertIn('--open-staged-native-saveas-lab', self.native)
        self.assertIn('--open-staged-native-saveas-event-v2-lab', self.native)
        self.assertIn('{"schema", "getdoc-native-saveas-candidate-lab/1"}', self.native)
        self.assertIn('{"schema", "getdoc-native-saveas-pinned-event-lab/2"}', self.native)
        self.assertIn('"approvedForProduction": False', self.v2)


if __name__ == "__main__":
    unittest.main()
