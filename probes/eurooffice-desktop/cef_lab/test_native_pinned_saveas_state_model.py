"""GD-31J.27e: deterministic SaveAs v2 callback state-matrix tests.

No CEF, Qt, binary launch or user files. These tests specify fail-closed
transitions and produce synthetic diagnostics understood by the existing v2
receipt parser. Deferred until the approved offline test stage.
"""
import itertools
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_pinned_saveas_state_model as model
import native_pinned_saveas_event_v2 as v2
from test_native_pinned_saveas_event_v2 import SESSION, TAB, FIXTURE

SHA = "a" * 64


def run(*actions):
    machine = model.NativeV2State()
    for action in actions:
        if action == "arm":
            machine.arm(ready=True)
        elif action == "dialog":
            machine.dialog(valid=True)
        elif action == "wrong_format":
            machine.dialog(valid=False)
        elif action == "answer":
            machine.answer(ready=True)
        elif action == "on_save":
            machine.on_save(cancelled=False)
        elif action == "cancel":
            machine.on_save(cancelled=True)
        elif action == "timeout":
            machine.timeout()
        elif action == "close":
            machine.close()
        elif action == "source_changed":
            machine.source_unchanged = False
        else:
            raise AssertionError(action)
    return machine


class NativeV2StateMatrixTests(unittest.TestCase):
    def test_only_armed_single_request_answer_and_onsave_qualifies_candidate(self):
        candidate = run("arm", "dialog", "answer", "on_save")
        self.assertTrue(candidate.can_publish())
        self.assertEqual((1, 1, 1, 2), candidate.sequence())
        self.assertEqual("candidate_untrusted", candidate.reason(published=True))
        report = candidate.receipt(SESSION, TAB, FIXTURE, published=True, sha=SHA)
        parsed = v2.parse(json.dumps(report, separators=(",", ":")),
                          SESSION, TAB, FIXTURE)
        self.assertTrue(parsed["nativeCallbackSequenceSelfReported"])
        self.assertFalse(parsed["approvedForProduction"])
        self.assertFalse(parsed["documentVerified"])

    def test_early_callback_and_late_callbacks_never_arm_again(self):
        for actions, code in (
            (("dialog", "arm", "answer", "on_save"), "invalid_dialog"),
            (("on_save", "arm", "dialog", "answer"), "unexpected_on_save"),
            (("arm", "dialog", "answer", "on_save", "dialog"), "duplicate_dialog"),
            (("arm", "dialog", "answer", "on_save", "on_save"), "duplicate_on_save"),
            (("arm", "dialog", "dialog", "answer", "on_save"), "duplicate_dialog"),
            (("arm", "wrong_format", "answer", "on_save"), "invalid_dialog"),
            (("arm", "dialog", "answer", "cancel"), "cancelled"),
            (("arm", "dialog", "answer", "timeout", "on_save"), "unexpected_on_save"),
            (("arm", "dialog", "answer", "on_save", "timeout"), "timeout"),
            (("arm", "dialog", "answer", "on_save", "source_changed"), "source_modified"),
        ):
            with self.subTest(actions=actions):
                s = run(*actions)
                self.assertFalse(s.can_publish(), actions)
                self.assertEqual(code, s.reason(published=False), actions)
                report = s.receipt(SESSION, TAB, FIXTURE, published=False)
                result = v2.parse(json.dumps(report, separators=(",", ":")),
                                  SESSION, TAB, FIXTURE)
                self.assertEqual(code, result["failureCode"])
                self.assertEqual("unconfirmed", result["diagnosticState"])
                self.assertFalse(result["nativeCallbackSequenceSelfReported"])

    def test_not_ready_cannot_arm_and_cannot_accept_dialog(self):
        s = model.NativeV2State()
        s.arm(ready=False)
        s.dialog(valid=True)
        self.assertFalse(s.can_publish())
        self.assertEqual("invalid_dialog", s.reason(published=False))
        self.assertFalse(s.answer(ready=True))

    def test_shutdown_is_final_and_timeout_is_sticky(self):
        for actions in (
            ("arm", "dialog", "answer", "timeout", "close", "on_save"),
            ("arm", "dialog", "answer", "on_save", "close", "dialog"),
        ):
            s = run(*actions)
            self.assertFalse(s.can_publish())
        s = run("arm", "dialog", "answer", "on_save", "timeout")
        s.arm(ready=True)
        self.assertFalse(s.can_publish())
        self.assertEqual("timeout", s.reason(published=False))

    def test_no_permutation_of_four_events_except_happy_path_may_publish(self):
        actions = ("arm", "dialog", "answer", "on_save")
        for order in itertools.permutations(actions):
            with self.subTest(order=order):
                s = run(*order)
                self.assertEqual(order == actions, s.can_publish())

    def test_bounded_counters_fail_closed_on_callback_storm(self):
        s = run("arm", "dialog", "answer")
        for _ in range(130):
            s.on_save(cancelled=False)
        self.assertFalse(s.can_publish())
        self.assertEqual("duplicate_on_save", s.reason(published=False))
        self.assertLessEqual(s.on_save_count, model.MAX_EVENTS)
        self.assertLessEqual(s.event_serial, model.MAX_EVENTS)


if __name__ == "__main__":
    unittest.main()
