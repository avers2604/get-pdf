"""GD-31J.27c: pinned native event-v2 parser. Deferred offline unit tests."""
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_pinned_saveas_event_v2 as v2

SESSION = "102030405060708090a0b0c0d0e0f010"
TAB = "f010e0d0c0b0a0908070605040302010"
FIXTURE = "gd31j6_writer.docx"
SHA = "c" * 64


def nested(**changes):
    result = {
        "schema": "getdoc-native-saveas-candidate-lab/1",
        "sessionId": SESSION, "tabId": TAB, "fixtureName": FIXTURE,
        "nativeSaveRequestObserved": True, "savePathProvided": True,
        "nativeOnSaveAfterAnswer": True, "saveCancelledAfterAnswer": False,
        "sourceUnchanged": True, "publishedToDisposableOutput": True,
        "outputSha256": SHA,
        "nativeSaveProven": False, "documentVerified": False,
        "saveVerified": False, "pdfVerified": False, "printApproved": False,
        "approvedForProduction": False, "laboratoryOnly": True,
    }
    result.update(changes)
    return result


def envelope(**changes):
    result = {
        "schema": "getdoc-native-saveas-pinned-event-lab/2",
        "sessionId": SESSION, "tabId": TAB, "fixtureName": FIXTURE,
        "failureCode": "candidate_untrusted", "phase": "published_disposable",
        "dialogCount": 1, "onSaveCount": 1,
        "dialogSequence": 1, "onSaveSequence": 2,
        "invalidDialogObserved": False, "unexpectedOnSaveObserved": False,
        "receipt": nested(),
    }
    result.update(changes)
    return result


class PinnedNativeEventV2Tests(unittest.TestCase):
    def parse(self, evidence):
        return v2.parse(json.dumps(evidence, separators=(",", ":")),
                        SESSION, TAB, FIXTURE)

    def test_successful_synthetic_candidate_is_still_untrusted(self):
        report = self.parse(envelope())
        self.assertEqual("getdoc-native-saveas-pinned-event-lab/2", report["schema"])
        self.assertEqual("candidate_untrusted", report["failureCode"])
        self.assertTrue(report["nativeCallbackSequenceSelfReported"])
        self.assertFalse(report["nativeSaveProven"])
        self.assertFalse(report["documentVerified"])
        self.assertFalse(report["approvedForProduction"])
        self.assertEqual(SHA, report["outputSha256"])

    def test_duplicate_after_answer_denies_even_when_legacy_flags_conflict(self):
        report = self.parse(envelope(
            failureCode="duplicate_dialog", phase="on_save_observed",
            dialogCount=2, onSaveCount=1, dialogSequence=1, onSaveSequence=2,
            receipt=nested(nativeSaveRequestObserved=False,
                           publishedToDisposableOutput=False, outputSha256="")))
        self.assertEqual("duplicate_dialog", report["failureCode"])
        self.assertFalse(report["nativeCallbackSequenceSelfReported"])
        self.assertEqual("", report["outputSha256"])

    def test_unexpected_on_save_before_answer_is_explicitly_rejected(self):
        report = self.parse(envelope(
            failureCode="unexpected_on_save", phase="on_save_observed",
            dialogCount=1, onSaveCount=1, dialogSequence=2, onSaveSequence=1,
            unexpectedOnSaveObserved=True,
            receipt=nested(savePathProvided=False, nativeOnSaveAfterAnswer=False,
                           publishedToDisposableOutput=False, outputSha256="")))
        self.assertEqual("unexpected_on_save", report["failureCode"])

    def test_timeout_and_cancel_are_never_success(self):
        for changes in (
            {
                "failureCode": "timeout", "phase": "target_assigned",
                "onSaveCount": 0, "onSaveSequence": 0,
                "receipt": nested(nativeOnSaveAfterAnswer=False,
                                  publishedToDisposableOutput=False, outputSha256=""),
            },
            {
                "failureCode": "cancelled", "phase": "on_save_observed",
                "receipt": nested(nativeOnSaveAfterAnswer=False,
                                  saveCancelledAfterAnswer=True,
                                  publishedToDisposableOutput=False, outputSha256=""),
            },
        ):
            with self.subTest(changes=changes):
                r = self.parse(envelope(**changes))
                self.assertEqual("unconfirmed", r["diagnosticState"])

    def test_forged_privileges_counts_sequences_and_schema_refused(self):
        base = envelope()
        for candidate in (
            envelope(receipt=nested(approvedForProduction=True)),
            envelope(receipt=nested(nativeSaveProven=True)),
            envelope(receipt=nested(outputSha256="e" * 64)),
            envelope(dialogCount=2),
            envelope(onSaveCount=2),
            envelope(dialogSequence=2, onSaveSequence=1),
            envelope(invalidDialogObserved=True),
            envelope(failureCode="not-a-real-code"),
            envelope(phase="quarantined"),
            envelope(receipt=nested(nativeOnSaveAfterAnswer=False)),
            envelope(receipt=nested(publishedToDisposableOutput=False, outputSha256="")),
            envelope(**{"unknown": "file"}),
            envelope(sessionId="f" * 32),
            envelope(tabId="0" * 32),
            envelope(fixtureName="customer.docx"),
            envelope(receipt=nested(sessionId=TAB)),
            envelope(receipt=nested(nativeOnSaveAfterAnswer=1)),
        ):
            with self.subTest(candidate=str(candidate)[:70]), self.assertRaises(v2.PinnedEventError):
                self.parse(candidate)

    def test_duplicate_keys_and_multiline_rejected(self):
        raw = json.dumps(envelope(), separators=(",", ":"))
        for candidate in (
            raw.replace('"schema":', '"schema":"forged","schema":', 1),
            raw.replace('"receipt":{', '"receipt":{"schema":"forged",', 1),
            raw + "\n" + raw,
            "x" * 5000,
            "[]",
        ):
            with self.assertRaises(v2.PinnedEventError):
                v2.parse(candidate, SESSION, TAB, FIXTURE)


if __name__ == "__main__":
    unittest.main()
