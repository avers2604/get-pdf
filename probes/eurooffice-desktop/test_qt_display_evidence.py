"""GD-31J.5 local evidence validator tests (no Qt runtime required)."""
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import validate_qt_display as validator


def make_report(*, manual=False, screen_count=1, switched=0, focus=0):
    return {
        "protocol": "getdoc-qt-display-diagnostics/1",
        "runtimeKind": "qt-only-no-cef",
        "closed": True,
        "cefInitialized": False,
        "realMonitorVerified": False,
        "manualMode": manual,
        "screenCount": screen_count,
        "screens": [
            {"width": 1920, "height": 1080, "devicePixelRatio": 1.5,
             "logicalDpi": 120.0} for _ in range(screen_count)],
        "windowEvents": {"show": 1, "resize": 2, "move": 2,
                         "focus": focus, "screenChanged": switched, "dpiChanged": 0},
    }


class DisplayEvidenceTests(unittest.TestCase):
    def test_offscreen_report_is_not_hardware_acceptance(self):
        with self.assertRaises(validator.EvidenceRejected):
            validator.require_physical_display_evidence(make_report())

    def test_two_screens_without_movement_is_not_hardware_acceptance(self):
        with self.assertRaises(validator.EvidenceRejected):
            validator.require_physical_display_evidence(
                make_report(manual=True, screen_count=2, switched=0, focus=1))

    def test_manual_two_screen_movement_only_meets_preliminary_gate(self):
        evidence = validator.require_physical_display_evidence(
            make_report(manual=True, screen_count=2, switched=1, focus=1))
        self.assertEqual("preliminary-manual-evidence", evidence["status"])
        self.assertFalse(evidence["cefVerified"])
        self.assertFalse(evidence["avaloniaEmbedded"])
        self.assertFalse(evidence["releaseReady"])

    def test_rejects_forged_success_and_invalid_dpi(self):
        bad = make_report(manual=True, screen_count=2, switched=1, focus=1)
        bad["realMonitorVerified"] = True
        with self.assertRaises(validator.EvidenceRejected):
            validator.require_physical_display_evidence(bad)
        bad["realMonitorVerified"] = False
        bad["screens"][0]["devicePixelRatio"] = 0
        with self.assertRaises(validator.EvidenceRejected):
            validator.require_physical_display_evidence(bad)

    def test_rejects_missing_closure_and_cef_claims(self):
        bad = make_report(manual=True, screen_count=2, switched=1, focus=1)
        bad["closed"] = False
        with self.assertRaises(validator.EvidenceRejected):
            validator.require_physical_display_evidence(bad)
        bad["closed"] = True
        bad["cefInitialized"] = True
        with self.assertRaises(validator.EvidenceRejected):
            validator.require_physical_display_evidence(bad)

    def test_does_not_accept_extra_fields_or_non_integer_metrics(self):
        bad = make_report(manual=True, screen_count=2, switched=1, focus=1)
        bad["extra"] = "unknown"
        with self.assertRaises(validator.EvidenceRejected):
            validator.require_physical_display_evidence(bad)
        bad.pop("extra")
        bad["windowEvents"]["screenChanged"] = True
        with self.assertRaises(validator.EvidenceRejected):
            validator.require_physical_display_evidence(bad)


if __name__ == "__main__":
    unittest.main()
