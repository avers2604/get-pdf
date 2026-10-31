"""GD-31J.27: strict intersection of native SaveAs receipt and independent OOXML bytes.

These tests are registered for future OFFLINE LOCAL acceptance; do not run
Qt, CEF, save real documents, call servers, or enable production behavior.
"""
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_ooxml_join as join
import synthetic_roundtrip as package_check
import test_synthetic_roundtrip as fixture_helpers

SESSION = "102030405060708090a0b0c0d0e0f010"
TAB = "f010e0d0c0b0a0908070605040302010"


def native_receipt(name, output_sha, **changes):
    receipt = {
        "schema": "getdoc-native-saveas-candidate-lab/1",
        "sessionId": SESSION,
        "tabId": TAB,
        "fixtureName": name,
        "nativeSaveRequestObserved": True,
        "savePathProvided": True,
        "nativeOnSaveAfterAnswer": True,
        "saveCancelledAfterAnswer": False,
        "sourceUnchanged": True,
        "publishedToDisposableOutput": True,
        "outputSha256": output_sha,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "pdfVerified": False,
        "printApproved": False,
        "approvedForProduction": False,
        "laboratoryOnly": True
    }
    receipt.update(changes)
    return json.dumps(receipt, separators=(",", ":"))


def setup_case(tmp, name):
    root, output, original = fixture_helpers.environment(tmp, name)
    sha = hashlib.sha256(output.read_bytes()).hexdigest()
    return root, output, original, native_receipt(name, sha)


class NativeOoxmlJoinTests(unittest.TestCase):
    def test_all_three_synthetic_formats_correlate_without_production_rights(self):
        for name in package_check.FIXTURES:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as tmp:
                root, output, original, receipt = setup_case(tmp, name)
                combined = join.correlate(root, name, SESSION, TAB, receipt)
                self.assertEqual("getdoc-native-ooxml-joined-candidate-lab/1",
                                 combined["schema"])
                self.assertTrue(combined["nativeEventChainSelfReported"])
                self.assertTrue(combined["independentPackageVerified"])
                self.assertTrue(combined["syntheticCanaryVerified"])
                self.assertTrue(combined["outputDigestCorrelated"])
                self.assertTrue(combined["sourceUnchanged"])
                self.assertEqual(hashlib.sha256(output.read_bytes()).hexdigest(),
                                 combined["outputSha256"])
                self.assertFalse(combined["nativeSaveProven"])
                self.assertFalse(combined["documentVerified"])
                self.assertFalse(combined["saveVerified"])
                self.assertFalse(combined["approvedForProduction"])
                self.assertEqual(original, (root / name).read_bytes())

    def test_wrong_session_tab_fixture_or_output_digest_is_rejected(self):
        name = "gd31j6_writer.docx"
        with tempfile.TemporaryDirectory() as tmp:
            root, _, _, receipt = setup_case(tmp, name)
            forged = json.loads(receipt)
            for key, val in (
                ("sessionId", TAB),
                ("tabId", SESSION),
                ("fixtureName", "gd31j6_calc.xlsx"),
                ("outputSha256", "f" * 64),
            ):
                with self.subTest(key=key):
                    candidate = dict(forged, **{key: val})
                    with self.assertRaises(join.JoinError):
                        join.correlate(root, name, SESSION, TAB, json.dumps(candidate))

    def test_cancel_missing_callback_missing_path_or_unpublished_is_rejected(self):
        name = "gd31j6_calc.xlsx"
        with tempfile.TemporaryDirectory() as tmp:
            root, _, _, receipt = setup_case(tmp, name)
            digest = json.loads(receipt)["outputSha256"]
            for changes in (
                {"saveCancelledAfterAnswer": True},
                {"nativeOnSaveAfterAnswer": False},
                {"savePathProvided": False},
                {"nativeSaveRequestObserved": False},
                {"publishedToDisposableOutput": False, "outputSha256": ""},
                {"sourceUnchanged": False},
            ):
                with self.subTest(changes=changes):
                    with self.assertRaises(join.JoinError):
                        join.correlate(root, name, SESSION, TAB,
                                       native_receipt(name, digest, **changes))

    def test_forged_privileges_unknown_fields_duplicate_keys_or_type_coercions_rejected(self):
        name = "gd31j6_impress.pptx"
        with tempfile.TemporaryDirectory() as tmp:
            root, _, _, receipt = setup_case(tmp, name)
            for candidate in (
                native_receipt(name, json.loads(receipt)["outputSha256"], nativeSaveProven=True),
                native_receipt(name, json.loads(receipt)["outputSha256"], documentVerified=True),
                native_receipt(name, json.loads(receipt)["outputSha256"], saveVerified=True),
                native_receipt(name, json.loads(receipt)["outputSha256"], approvedForProduction=True),
                native_receipt(name, json.loads(receipt)["outputSha256"], laboratoryOnly=False),
                native_receipt(name, json.loads(receipt)["outputSha256"], nativeOnSaveAfterAnswer=1),
                native_receipt(name, json.loads(receipt)["outputSha256"], nativeSaveRequestObserved="true"),
                native_receipt(name, json.loads(receipt)["outputSha256"], extraSecret="foo"),
                receipt.replace('"schema":', '"schema":"duplicate","schema":', 1),
                '{"schema":"bad"}',
                receipt + "garbage",
                receipt + "\n" + receipt,
                "x" * 2049,
            ):
                with self.subTest(candidate=candidate[:55]):
                    with self.assertRaises(join.JoinError):
                        join.correlate(root, name, SESSION, TAB, candidate)

    def test_ooxml_canary_loss_corruption_or_source_mutation_fail(self):
        name = "gd31j6_writer.docx"
        with tempfile.TemporaryDirectory() as tmp:
            root, output, original, receipt = setup_case(tmp, name)
            output.write_bytes(fixture_helpers.package(name, "NOT_THE_CANARY"))
            with self.assertRaises(join.JoinError):
                join.correlate(root, name, SESSION, TAB, receipt)
            output.write_bytes(b"not-a-zip")
            with self.assertRaises(join.JoinError):
                join.correlate(root, name, SESSION, TAB, receipt)
            output.write_bytes(fixture_helpers.package(name, package_check.CANARIES[name]))
            (root / name).write_bytes(original + b"change")
            with self.assertRaises(join.JoinError):
                join.correlate(root, name, SESSION, TAB, receipt)

    def test_stdin_requires_one_complete_bounded_receipt(self):
        name = "gd31j6_writer.docx"
        with tempfile.TemporaryDirectory() as tmp:
            root, _, _, receipt = setup_case(tmp, name)
            self.assertTrue(join.correlate_from_stream(
                root, name, SESSION, TAB, io.BytesIO((receipt + "\n").encode())
            )["outputDigestCorrelated"])
            for data in (
                receipt.encode(),
                (receipt + "\n\n").encode(),
                (receipt + "\n" + receipt + "\n").encode(),
                b"\x00" + receipt.encode() + b"\n",
                b"x" * 3000,
            ):
                with self.assertRaises(join.JoinError):
                    join.correlate_from_stream(root, name, SESSION, TAB, io.BytesIO(data))

    def test_no_input_from_customer_paths_or_network_sources(self):
        name = "gd31j6_writer.docx"
        with tempfile.TemporaryDirectory() as tmp:
            root, _, _, receipt = setup_case(tmp, name)
            for wrong in ("../gd31j6_writer.docx", "customer.docx",
                          "https://example.org/file.docx"):
                with self.assertRaises(join.JoinError):
                    join.correlate(root, wrong, SESSION, TAB, receipt)
            for bad_id in ("", "0" * 32, "ABCDEF" * 5 + "ab", "f" * 31, "foo"):
                with self.assertRaises(join.JoinError):
                    join.correlate(root, name, bad_id, TAB, receipt)


if __name__ == "__main__":
    unittest.main()
