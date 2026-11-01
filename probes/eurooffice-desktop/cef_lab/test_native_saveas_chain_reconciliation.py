"""GD31J27h: cross-component source/fixture/permission lineage contracts.

This is an offline source contract, not an executed CEF/SDK integration test.
The v1 candidate and pinned event v2 are separate protocols. A nested v1
object alone must never become an authenticated save or production grant.
"""
from pathlib import Path
import re
import unittest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


class NativeSaveAsChainReconciliationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp = (HERE / "cef_staged_open.cpp").read_text("utf-8")
        cls.policy = (HERE / "native_saveas_policy.h").read_text("utf-8")
        cls.durable = (HERE / "native_saveas_durable.h").read_text("utf-8")
        cls.ooxml = (HERE / "synthetic_roundtrip.py").read_text("utf-8")
        cls.v1 = (HERE / "native_ooxml_join.py").read_text("utf-8")
        cls.v2 = (HERE / "native_pinned_saveas_event_v2.py").read_text("utf-8")
        cls.join = (HERE / "native_pinned_event_ooxml_join.py").read_text("utf-8")
        cls.supervisor = (HERE / "native_pinned_v2_supervisor.py").read_text("utf-8")
        cls.orphans = (HERE / "native_saveas_orphan_inventory.py").read_text("utf-8")
        cls.net = (ROOT / "src/GetDocument.Office/EuroOfficeDesktop" /
                   "EuroOfficePinnedNativeSaveAsEventV2.cs").read_text("utf-8")

    def test_three_exact_synthetic_formats_have_one_shared_final_basename(self):
        import synthetic_roundtrip as ooxml
        self.assertEqual(3, len(ooxml.FIXTURES))
        for fixture in ooxml.FIXTURES:
            basename = ooxml.OUTPUTS[fixture]
            self.assertIn('"' + fixture + '"', self.policy)
            self.assertIn('"' + basename + '"', self.policy)
            self.assertIn('"' + basename + '"', self.durable)
            self.assertIn('"' + basename + '"', self.ooxml)
        self.assertIn('gd31j25-output-lab.marker', self.policy)
        self.assertIn('gd31j25-output-lab.marker', self.durable)
        self.assertIn('gd31j27g-private-owner.marker', self.durable)
        self.assertIn('gd31j27g-private-owner.marker', self.orphans)

    def test_exact_protocol_chaining_never_downgrades_pinned_v2_to_v1(self):
        self.assertIn('getdoc-native-saveas-candidate-lab/1', self.cpp)
        self.assertIn('getdoc-native-saveas-pinned-event-lab/2', self.cpp)
        self.assertIn('getdoc-native-saveas-candidate-lab/1', self.v1)
        self.assertIn('getdoc-native-saveas-pinned-event-lab/2', self.v2)
        self.assertIn('getdoc-native-pinned-v2-ooxml-joined-lab/1', self.join)
        self.assertIn('getdoc-native-pinned-v2-supervised-ooxml-lab/1',
                      self.supervisor)
        self.assertIn('joined_v2.from_stream(', self.supervisor)
        self.assertIn('old_join.correlate(', self.join)
        self.assertIn('not 3 <= len(data) <= event_v2.MAX_BYTES + 1', self.join)
        self.assertIn('EuroOfficeNativeSaveAsCandidate.Parse(', self.net)
        self.assertIn('!Flag(nested, "laboratoryOnly")', self.net)

    def test_crash_state_does_not_pretend_to_be_verified_native_save(self):
        self.assertIn('single_link=True', self.ooxml)
        self.assertIn('before.st_nlink != 1', self.ooxml)
        self.assertIn('linkedPublicationPendingReview', self.orphans)
        self.assertIn('safeToDeleteAutomatically": False', self.orphans)
        self.assertIn('AT_SYMLINK_NOFOLLOW', self.durable)
        self.assertIn('::linkat(', self.durable)
        self.assertIn('::fsync(output.value)', self.durable)
        self.assertNotIn('::unlink(', self.durable)
        self.assertNotIn('::unlink(', self.policy)
        self.assertNotIn('os.unlink(', self.orphans)
        self.assertNotIn('shutil.rmtree(', self.orphans)

    def test_native_candidate_and_dotnet_parser_both_deny_production_grants(self):
        for key in ("nativeSaveProven", "documentVerified", "saveVerified",
                    "approvedForProduction"):
            self.assertIn('{"' + key + '", false}', self.cpp)
            self.assertIn('"' + key + '": False', self.supervisor)
        for property_name in ("NativeSaveProven", "DocumentVerified",
                              "SaveVerified", "ApprovedForProduction"):
            self.assertIn("public bool " + property_name + " => false;", self.net)
        self.assertIn('nativeCallbackSequenceSelfReported', self.v2)
        self.assertNotIn('approvedForProduction": True', self.supervisor)


if __name__ == "__main__":
    unittest.main()
