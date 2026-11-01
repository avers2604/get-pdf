"""GD31J27i: deferred offline release-gate tests. No SDK, VM or file writes."""
from pathlib import Path
import json
import sys
import tempfile
import unittest

LAB = Path(__file__).resolve().parent
sys.path.insert(0, str(LAB))
sys.path.insert(0, str(LAB.parents[2] / "scripts"))
import native_saveas_acceptance_gate as gate
import office_acceptance as acceptance

REVISION = "a" * 40
SHA = "b" * 64


def automated_report():
    steps = acceptance.build_steps(
        Path("/get-document"), Path("/offline-nuget"), "Linux")
    return {
        "schemaVersion": 1,
        "stage": "GD-31I",
        "revision": REVISION,
        "host": "Linux",
        "status": "automated-checks-passed",
        "automatedChecksPassed": True,
        "releaseReady": False,
        "manualAcceptance": {
            name: "not-run" for name in acceptance.MANUAL_GATES},
        "steps": [
            {"name": s.name, "status": "passed", "exitCode": 0}
            for s in steps
        ],
    }


def evidence(*, all_pass=False):
    return {
        "schema": gate.EVIDENCE_SCHEMA,
        "revision": REVISION,
        "protocol": "getdoc-native-saveas-pinned-event-lab/2",
        "fixtureNames": list(gate.FIXTURES),
        "isolatedVm": {
            "offline": True,
            "disposable": True,
            "corporateMountsAbsent": True,
            "privilegedSecretsAbsent": True,
        },
        "cases": {
            name: {
                "status": "passed" if all_pass else "not-run",
                "evidenceSha256": SHA if all_pass else "",
                "reviewedByOperator": bool(all_pass),
            } for name in gate.CASES
        },
    }


class NativeSaveAsAcceptanceGateTests(unittest.TestCase):
    def test_plan_includes_all_fault_cases_without_authorization(self):
        result = gate.plan()
        self.assertEqual(gate.PLAN_SCHEMA, result["schema"])
        self.assertEqual(set(gate.CASES), set(result["cases"]))
        self.assertTrue(all(v == "not-run" for v in result["cases"].values()))
        self.assertFalse(result["releaseReady"])
        self.assertFalse(result["approvedForProduction"])
        self.assertFalse(result["writePermissionGranted"])

    def test_unrun_manual_checks_are_blocked(self):
        result = gate.evaluate(automated_report(), evidence())
        self.assertEqual("blocked", result["reviewState"])
        self.assertEqual(0, result["operatorClaimsPassed"])
        self.assertEqual(len(gate.CASES), len(result["blockingCases"]))
        self.assertFalse(result["releaseReady"])

    def test_even_all_claims_passing_cannot_grant_save_or_release_rights(self):
        result = gate.evaluate(automated_report(), evidence(all_pass=True))
        self.assertEqual("requires-independent-review", result["reviewState"])
        self.assertEqual(len(gate.CASES), result["operatorClaimsPassed"])
        self.assertFalse(result["releaseReady"])
        self.assertFalse(result["nativeSaveProven"])
        self.assertFalse(result["documentVerified"])
        self.assertFalse(result["saveVerified"])
        self.assertFalse(result["approvedForProduction"])
        self.assertFalse(result["writePermissionGranted"])
        self.assertFalse(result["safeToDeleteAutomatically"])

    def test_invalid_evidence_refuses_unapproved_status_and_false_isolation(self):
        for failure in ("unknown_case", "missing_case", "unknown_status",
                        "fake_pass_without_sha", "fake_pass_without_review",
                        "bad_revision", "no_offline", "no_disposable",
                        "corporate_mounted", "wrong_protocol", "wrong_fixture",
                        "extra_field", "production_claim"):
            with self.subTest(failure=failure):
                case = evidence(all_pass=True)
                if failure == "unknown_case":
                    case["cases"]["user-file-can-be-overwritten"] = {
                        "status": "passed", "evidenceSha256": SHA,
                        "reviewedByOperator": True}
                elif failure == "missing_case":
                    case["cases"].pop(gate.CASES[0])
                elif failure == "unknown_status":
                    case["cases"][gate.CASES[0]]["status"] = "approved"
                elif failure == "fake_pass_without_sha":
                    case["cases"][gate.CASES[0]]["evidenceSha256"] = ""
                elif failure == "fake_pass_without_review":
                    case["cases"][gate.CASES[0]]["reviewedByOperator"] = False
                elif failure == "bad_revision":
                    case["revision"] = "c" * 40
                elif failure == "no_offline":
                    case["isolatedVm"]["offline"] = False
                elif failure == "no_disposable":
                    case["isolatedVm"]["disposable"] = False
                elif failure == "corporate_mounted":
                    case["isolatedVm"]["corporateMountsAbsent"] = False
                elif failure == "wrong_protocol":
                    case["protocol"] = "getdoc-native-saveas-candidate-lab/1"
                elif failure == "wrong_fixture":
                    case["fixtureNames"][0] = "contract.docx"
                elif failure == "extra_field":
                    case["metadata"] = {"secrets": "unknown"}
                elif failure == "production_claim":
                    case["approvedForProduction"] = True
                with self.assertRaises(gate.GateError):
                    gate.evaluate(automated_report(), case)

    def test_automated_report_false_claims_missing_steps_or_wrong_revision_rejected(self):
        for failure in ("auto_false", "release_true", "status_wrong",
                        "wrong_revision", "missing_step", "step_failed",
                        "manual_forged", "duplicate_step", "step_nonzero"):
            with self.subTest(failure=failure):
                report = automated_report()
                if failure == "auto_false":
                    report["automatedChecksPassed"] = False
                elif failure == "release_true":
                    report["releaseReady"] = True
                elif failure == "status_wrong":
                    report["status"] = "plan-only"
                elif failure == "wrong_revision":
                    report["revision"] = "c" * 40
                elif failure == "missing_step":
                    report["steps"].pop()
                elif failure == "step_failed":
                    report["steps"][0]["status"] = "failed"
                elif failure == "manual_forged":
                    report["manualAcceptance"][gate.REQUIRED_MANUAL_GATES[0]] = "passed"
                elif failure == "duplicate_step":
                    report["steps"].append(report["steps"][0])
                elif failure == "step_nonzero":
                    report["steps"][0]["exitCode"] = 1
                with self.assertRaises(gate.GateError):
                    gate.evaluate(report, evidence(all_pass=True))

    def test_read_only_evidence_loader_refuses_symlinks_duplicates_and_oversize(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            evidence_file = root / "evidence.json"
            evidence_file.write_text(json.dumps(evidence()), encoding="utf8")
            self.assertEqual(gate.EVIDENCE_SCHEMA,
                             gate.read_json_file(evidence_file)["schema"])
            alias = root / "alias.json"
            alias.symlink_to(evidence_file)
            with self.assertRaises(gate.GateError):
                gate.read_json_file(alias)
            evidence_file.write_text(
                '{"schema":"a","schema":"b"}', encoding="utf8")
            with self.assertRaises(gate.GateError):
                gate.read_json_file(evidence_file)
            evidence_file.write_bytes(b"x" * (gate.MAX_JSON_BYTES + 1))
            with self.assertRaises(gate.GateError):
                gate.read_json_file(evidence_file)


if __name__ == "__main__":
    unittest.main()
