"""GD-31J.27c: independent OOXML join of optional event-v2 receipts (deferred)."""
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_pinned_event_ooxml_join as event_join
import native_pinned_saveas_event_v2 as v2
import synthetic_roundtrip as ooxml
import test_synthetic_roundtrip as synthetic
from test_native_pinned_saveas_event_v2 import SESSION, TAB, envelope


class NativePinnedEventV2OoxmlJoinTests(unittest.TestCase):
    def test_all_three_formats_require_real_independent_ooxml_canary(self):
        for name in ooxml.FIXTURES:
            with self.subTest(fixture=name), tempfile.TemporaryDirectory() as tmp:
                root, output, _ = synthetic.environment(tmp, name)
                evidence = envelope()
                evidence["fixtureName"] = name
                evidence["receipt"]["fixtureName"] = name
                evidence["receipt"]["outputSha256"] = hashlib.sha256(
                    output.read_bytes()).hexdigest()
                result = event_join.verify(
                    root, name, SESSION, TAB, json.dumps(evidence))
                self.assertEqual(event_join.SCHEMA, result["schema"])
                self.assertEqual(name, result["fixtureName"])
                self.assertTrue(result["independentOoxmlCorrelated"])
                self.assertTrue(result["nativeEventOrderSelfReported"])
                self.assertFalse(result["approvedForProduction"])
                self.assertFalse(result["documentVerified"])

    def test_forged_output_hash_never_passes_independent_verify(self):
        name = ooxml.FIXTURES[0]
        with tempfile.TemporaryDirectory() as tmp:
            root, _, _ = synthetic.environment(tmp, name)
            with self.assertRaises(event_join.V2JoinError):
                event_join.verify(root, name, SESSION, TAB, json.dumps(envelope()))

    def test_duplicate_dialog_failure_never_becomes_verified(self):
        name = ooxml.FIXTURES[0]
        with tempfile.TemporaryDirectory() as tmp:
            root, _, _ = synthetic.environment(tmp, name)
            data = envelope(
                failureCode="duplicate_dialog", phase="on_save_observed",
                dialogCount=2, onSaveCount=1,
                receipt=dict(envelope()["receipt"],
                             nativeSaveRequestObserved=False,
                             publishedToDisposableOutput=False,
                             outputSha256=""))
            with self.assertRaises(event_join.V2JoinError):
                event_join.verify(root, name, SESSION, TAB, json.dumps(data))

    def test_one_bounded_event_v2_line_only(self):
        name = ooxml.FIXTURES[0]
        with tempfile.TemporaryDirectory() as tmp:
            root, output, _ = synthetic.environment(tmp, name)
            data = envelope()
            data["receipt"]["outputSha256"] = hashlib.sha256(output.read_bytes()).hexdigest()
            encoded = (json.dumps(data) + "\n").encode("ascii")
            result = event_join.from_stream(root, name, SESSION, TAB, io.BytesIO(encoded))
            self.assertTrue(result["independentOoxmlCorrelated"])
            for bad in (encoded[:-1], encoded + encoded, encoded + b"x",
                        b"x" * 5000):
                with self.assertRaises(event_join.V2JoinError):
                    event_join.from_stream(root, name, SESSION, TAB, io.BytesIO(bad))


if __name__ == "__main__":
    unittest.main()
