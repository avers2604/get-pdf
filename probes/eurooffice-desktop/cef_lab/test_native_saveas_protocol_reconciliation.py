"""GD-31J.27b: tests of mutually exclusive, diagnostic-only SaveAs protocols.

Fixtures are synthetic JSON, not CEF processes or real documents.
"""
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_protocol_reconciliation as reconcile

SESSION = "102030405060708090a0b0c0d0e0f010"
TAB = "f010e0d0c0b0a0908070605040302010"
FILE = "gd31j6_writer.docx"
SOURCE = "a" * 64
OUTPUT = "b" * 64


def current_receipt(**changes):
    x = {
        "schema": "getdoc-native-saveas-candidate-lab/1",
        "sessionId": SESSION, "tabId": TAB, "fixtureName": FILE,
        "nativeSaveRequestObserved": True, "savePathProvided": True,
        "nativeOnSaveAfterAnswer": True, "saveCancelledAfterAnswer": False,
        "sourceUnchanged": True, "publishedToDisposableOutput": True,
        "outputSha256": OUTPUT, "nativeSaveProven": False,
        "documentVerified": False, "saveVerified": False,
        "pdfVerified": False, "printApproved": False,
        "approvedForProduction": False, "laboratoryOnly": True
    }
    x.update(changes)
    return x


def legacy_receipt(**changes):
    core = {
        "schema": "getdoc-native-saveas-private-lab/1",
        "sessionId": SESSION, "tabId": TAB, "fixtureName": FILE,
        "selectedFormat": "docx", "sourceBeforeSha256": SOURCE,
        "sourceAfterSha256": SOURCE, "saveAsRequestSeen": True,
        "saveAsTargetAccepted": True, "saveNotification": True,
        "saveCancelled": False, "candidateWritten": True,
        "candidateName": "gd31j26-saveas-candidate-" + SESSION + ".docx",
        "candidateLength": 1024, "candidateSha256": OUTPUT,
        "documentVerified": False, "saveVerified": False,
        "pdfVerified": False, "printApproved": False,
        "laboratoryOnly": True
    }
    old = {
        "schema": "getdoc-native-saveas-event-lab/2",
        "sessionId": SESSION, "tabId": TAB, "fixtureName": FILE,
        "failureCode": "candidate_untrusted", "phase": "quarantined",
        "dialogCount": 1, "onSaveCount": 1,
        "dialogSequence": 1, "onSaveSequence": 2,
        "receipt": core
    }
    old.update(changes)
    return old


class ReconciliationTests(unittest.TestCase):
    def check_candidate(self, raw, protocol, sequencing):
        diagnostic = reconcile.reconcile(
            json.dumps(raw, separators=(",", ":")), SESSION, TAB, FILE)
        self.assertEqual(reconcile.RESULT_SCHEMA, diagnostic["schema"])
        self.assertEqual(protocol, diagnostic["sourceProtocol"])
        self.assertEqual("synthetic_candidate_untrusted", diagnostic["diagnosticState"])
        self.assertEqual(sequencing, diagnostic["nativeEventOrderSelfReported"])
        self.assertEqual(OUTPUT, diagnostic["outputSha256"])
        self.assertFalse(diagnostic["nativeSaveProven"])
        self.assertFalse(diagnostic["documentVerified"])
        self.assertFalse(diagnostic["saveVerified"])
        self.assertFalse(diagnostic["approvedForProduction"])
        self.assertTrue(diagnostic["laboratoryOnly"])
        self.assertNotIn("path", diagnostic)
        self.assertNotIn("candidateName", diagnostic)

    def test_each_existing_protocol_produces_only_a_separate_candidate_diagnostic(self):
        self.check_candidate(current_receipt(), "gd31j26-pinned/1", False)
        self.check_candidate(legacy_receipt(), "gd31j28-guarded/2", True)

    def test_current_cancel_or_missing_callback_remain_unspecified_failures(self):
        for changes in (
            {"nativeSaveRequestObserved": False, "savePathProvided": False,
             "nativeOnSaveAfterAnswer": False, "publishedToDisposableOutput": False,
             "outputSha256": ""},
            {"nativeOnSaveAfterAnswer": False, "publishedToDisposableOutput": False,
             "outputSha256": ""},
            {"saveCancelledAfterAnswer": True, "nativeOnSaveAfterAnswer": False,
             "publishedToDisposableOutput": False, "outputSha256": ""},
        ):
            with self.subTest(changes=changes):
                result = reconcile.reconcile(json.dumps(current_receipt(**changes)),
                                             SESSION, TAB, FILE)
                self.assertEqual("unconfirmed", result["diagnosticState"])
                self.assertEqual("unspecified_failure", result["reasonCode"])
                self.assertEqual("", result["outputSha256"])
                self.assertFalse(result["nativeEventOrderSelfReported"])

    def test_legacy_detailed_failure_remains_diagnostic_only(self):
        receipt = legacy_receipt()
        receipt["failureCode"] = "duplicate_dialog"
        receipt["phase"] = "target_assigned"
        receipt["dialogCount"] = 2
        receipt["onSaveCount"] = 0
        receipt["onSaveSequence"] = 0
        receipt["receipt"].update({
            "saveNotification": False, "candidateWritten": False,
            "candidateName": "", "candidateLength": 0, "candidateSha256": ""
        })
        result = reconcile.reconcile(json.dumps(receipt), SESSION, TAB, FILE)
        self.assertEqual("unconfirmed", result["diagnosticState"])
        self.assertEqual("duplicate_dialog", result["reasonCode"])
        self.assertFalse(result["nativeEventOrderSelfReported"])

    def test_cross_session_unknown_duplicate_json_and_mixed_schema_refuse(self):
        for fixture in (current_receipt(), legacy_receipt()):
            for change in ("sessionId", "tabId", "fixtureName"):
                modified = dict(fixture, **{change: "bad"})
                with self.subTest(change=change), self.assertRaises(reconcile.ProtocolError):
                    reconcile.reconcile(json.dumps(modified), SESSION, TAB, FILE)
            for modified in (
                dict(fixture, extra="secret"),
                dict(fixture, schema="getdoc-native-saveas-event-lab/2"
                     if fixture["schema"].endswith("candidate-lab/1") else
                     "getdoc-native-saveas-candidate-lab/1"),
            ):
                with self.assertRaises(reconcile.ProtocolError):
                    reconcile.reconcile(json.dumps(modified), SESSION, TAB, FILE)
            raw = json.dumps(fixture, separators=(",", ":"))
            with self.assertRaises(reconcile.ProtocolError):
                reconcile.reconcile(raw.replace('"schema":', '"schema":"duplicate","schema":', 1),
                                    SESSION, TAB, FILE)
            with self.assertRaises(reconcile.ProtocolError):
                reconcile.reconcile(raw + "\n" + raw, SESSION, TAB, FILE)

    def test_forged_privileges_bad_sha_invalid_boolean_and_invalid_sequences_refuse(self):
        for payload in (
            current_receipt(approvedForProduction=True),
            current_receipt(nativeSaveProven=True),
            current_receipt(outputSha256="123"),
            current_receipt(nativeOnSaveAfterAnswer="true"),
            current_receipt(publishedToDisposableOutput=False),
            legacy_receipt(dialogCount=2),
            legacy_receipt(dialogSequence=2, onSaveSequence=1),
            legacy_receipt(failureCode="candidate_untrusted", phase="target_assigned"),
            legacy_receipt(receipt=dict(legacy_receipt()["receipt"], saveVerified=True)),
            legacy_receipt(receipt=dict(legacy_receipt()["receipt"],
                                        candidateName="../corp.docx")),
            legacy_receipt(receipt=dict(legacy_receipt()["receipt"],
                                        selectedFormat="xlsx")),
            legacy_receipt(receipt=dict(legacy_receipt()["receipt"],
                                        sourceAfterSha256=OUTPUT)),
            legacy_receipt(receipt=dict(legacy_receipt()["receipt"],
                                        candidateLength=True)),
        ):
            with self.subTest(schema=payload["schema"]), self.assertRaises(reconcile.ProtocolError):
                reconcile.reconcile(json.dumps(payload), SESSION, TAB, FILE)

    def test_stdin_one_ascii_line_only(self):
        import io
        good = json.dumps(current_receipt()).encode("ascii") + b"\n"
        self.assertEqual("gd31j26-pinned/1", reconcile.from_stream(
            io.BytesIO(good), SESSION, TAB, FILE)["sourceProtocol"])
        for bad in (good[:-1], good + b"\n", good + good, good + b"extra",
                    b"\x00" + good, b"a" * 5000, good.replace(b"false", b"FALSE")):
            with self.subTest(length=len(bad)), self.assertRaises(reconcile.ProtocolError):
                reconcile.from_stream(io.BytesIO(bad), SESSION, TAB, FILE)


if __name__ == "__main__":
    unittest.main()
