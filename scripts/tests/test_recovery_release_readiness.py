"""GD-31J.27br: local, source-only behavior tests for the release blocker inventory."""
from pathlib import Path
import json
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import recovery_release_readiness as rr


GATES = (
    "gd31j27bq-astra-openat2-pinned-parent-manual-e2e",
    "eurooffice-native-saveas-pinned-sdk-output-isolation-e2e",
    "physical-get-print-wizard-a4-a3",
)
SHA = "a" * 40


class ReadinessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "scripts").mkdir()
        (self.root / "scripts/office_acceptance.py").write_text(
            "MANUAL_GATES = (\n" +
            "".join(f"    {gate!r},\n" for gate in GATES) + ")\n",
            encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def report(self, gates=None, *, revision=SHA, release_ready=False):
        value = {
            "schemaVersion": 1, "revision": revision,
            "host": "Linux", "stage": "GD-31I", "releaseReady": release_ready,
            "automatedChecksPassed": False,
            "manualAcceptance": dict(gates if gates is not None else
                                     ((name, "not-run") for name in GATES)),
        }
        target = self.root / "evidence.json"
        target.write_text(json.dumps(value), encoding="utf-8")
        return target

    def test_without_report_does_not_invent_passes_or_authorize_release(self):
        report = rr.analyze(self.root)
        self.assertEqual("blocked", report["status"])
        self.assertFalse(report["releaseReady"])
        self.assertEqual(3, report["manualGates"]["noEvidence"])
        self.assertEqual(1, report["groups"]["recovery"])
        self.assertEqual(1, report["groups"]["nativeEuroOffice"])
        self.assertEqual(1, report["groups"]["other"])

    def test_passed_string_is_only_claim_not_independent_evidence(self):
        p = self.report([(GATES[0], "passed"), (GATES[1], "failed"),
                         (GATES[2], "not-run")])
        before = p.read_bytes()
        result = rr.analyze(self.root, report_path=p, expected_revision=SHA, details=True)
        self.assertFalse(result["releaseReady"])
        self.assertEqual(1, result["manualGates"]["claimedPassedUnverified"])
        self.assertEqual(1, result["manualGates"]["failed"])
        self.assertEqual(1, result["manualGates"]["notRun"])
        self.assertEqual(3, len(result["blockingGates"]))
        self.assertEqual(before, p.read_bytes())

    def test_revision_mismatch_refuses_stale_report(self):
        p = self.report(revision="b" * 40)
        with self.assertRaises(ValueError):
            rr.analyze(self.root, report_path=p, expected_revision=SHA)

    def test_report_without_expected_revision_is_denied(self):
        with self.assertRaises(ValueError):
            rr.analyze(self.root, report_path=self.report())

    def test_missing_required_gate_and_unrecognized_state_are_blocked(self):
        p = self.report([(GATES[0], "passed"), (GATES[1], "green")])
        result = rr.analyze(self.root, report_path=p, expected_revision=SHA)
        self.assertEqual(1, result["manualGates"]["missing"])
        self.assertEqual(1, result["manualGates"]["invalid"])
        self.assertFalse(result["releaseReady"])

    def test_fake_release_ready_and_unknown_gate_create_warnings(self):
        p = self.report(list((x, "passed") for x in GATES) +
                        [("injected-unlisted-gate", "passed")], release_ready=True)
        result = rr.analyze(self.root, report_path=p, expected_revision=SHA)
        self.assertEqual(3, result["manualGates"]["claimedPassedUnverified"])
        self.assertIn("report-claims-release-ready", result["warnings"])
        self.assertIn("unknown-report-gates", result["warnings"])
        self.assertFalse(result["releaseReady"])

    def test_duplicate_or_dynamic_gate_definition_is_rejected(self):
        path = self.root / "scripts/office_acceptance.py"
        path.write_text("MANUAL_GATES = ('same', 'same')\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            rr.load_required_gates(self.root)
        path.write_text("MANUAL_GATES = tuple(['dynamic'])\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            rr.load_required_gates(self.root)

    def test_bad_report_schema_and_symlink_are_rejected(self):
        p = self.report()
        doc = json.loads(p.read_text(encoding="utf-8"))
        doc["schemaVersion"] = 99
        p.write_text(json.dumps(doc), encoding="utf-8")
        with self.assertRaises(ValueError):
            rr.analyze(self.root, report_path=p, expected_revision=SHA)
        p = self.report()
        link = self.root / "aliased.json"
        try:
            link.symlink_to(p)
        except OSError:
            return  # Windows without symlink permission
        with self.assertRaises(ValueError):
            rr.analyze(self.root, report_path=link, expected_revision=SHA)


if __name__ == "__main__":
    unittest.main()
