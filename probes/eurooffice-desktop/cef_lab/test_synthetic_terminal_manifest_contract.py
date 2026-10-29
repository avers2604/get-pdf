"""GD-31J.37 offline source contracts for CEF terminal failure and manifest shape.

Static source checks only: no .NET/Python execution, native Qt/CEF process,
filesystem mutation, document publication, or crash/power-loss verification.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"
TESTS = ROOT / "tests/GetDocument.Office.Tests"
NATIVE = ROOT / "probes/eurooffice-desktop/cef_lab/cef_staged_open.cpp"

class SyntheticEndToEndContractSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = NATIVE.read_text(encoding="utf-8")
        cls.receipt = (OFFICE / "EuroOfficeNativeSaveAsEventReceipt.cs").read_text(encoding="utf-8")
        cls.snapshot = (OFFICE / "EuroOfficeSyntheticReviewedSnapshot.cs").read_text(encoding="utf-8")
        cls.event_tests = (TESTS / "EuroOfficeNativeSaveAsEventReceiptTests.cs").read_text(encoding="utf-8")
        cls.lab_tests = (TESTS / "EuroOfficeNativeSaveAsLabTests.cs").read_text(encoding="utf-8")
        cls.manifest_tests = (TESTS / "EuroOfficeSyntheticManifestIntegrityTests.cs").read_text(encoding="utf-8")

    def test_native_can_report_timeout_after_onsave_but_parser_never_calls_it_saved(self):
        cpp = self.native
        start = cpp.index("const QString nativePhase =")
        stop = cpp.index("const QJsonObject receiptV2{", start)
        snippet = cpp[start:stop]
        self.assertIn('onSaveCount > 0 ? "on_save_observed"', snippet)
        self.assertIn('saveAsLifetimeTimeout ? "timeout"', snippet)
        self.assertLess(snippet.index('saveAsLifetimeTimeout ? "timeout"'),
                        snippet.index('onSaveCount == 0 ? "save_not_acknowledged"'))
        self.assertIn('code == "timeout" && phase == "not_started"', self.receipt)
        self.assertNotIn('code == "timeout" && onSaveCount != 0', self.receipt)
        self.assertIn('SaveCompleted => false', self.receipt)
        self.assertIn('AuthorizedForPublication => false', self.receipt)
        self.assertIn('TimeoutAfterObservedOnSaveRemainsAValidFailureNotASave', self.event_tests)
        self.assertIn('PostOnSaveTimeoutRetainsFailureThroughCandidateReviewWithoutWriting',
                      self.lab_tests)

    def test_manifest_header_is_strict_before_any_journal_or_hardlink(self):
        source = self.snapshot
        self.assertIn('root.ValueKind != JsonValueKind.Object', source)
        self.assertIn('var rootNames = new HashSet<string>(StringComparer.Ordinal)', source)
        self.assertIn('foreach (JsonProperty property in root.EnumerateObject())', source)
        self.assertIn('!rootNames.Add(property.Name)', source)
        self.assertIn('property.Name is not ("schema" or "purpose" or "files")', source)
        self.assertIn('rootNames.Count != 3', source)
        self.assertIn('root.GetProperty("schema").ValueKind != JsonValueKind.String', source)
        self.assertIn('root.GetProperty("purpose").ValueKind != JsonValueKind.String', source)
        self.assertLess(source.index('RequireManifest(manifest, native.Core.FixtureName,'),
                        source.index('EuroOfficeSyntheticSnapshotJournal.Begin('))
        self.assertIn('DuplicateOrUnknownManifestRootFieldsMustFailBeforeAnyJournal',
                      self.manifest_tests)
        self.assertIn('Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length)',
                      self.manifest_tests)

    def test_existing_publication_denials_and_final_directory_order_are_unchanged(self):
        for marker in (
            'public bool WormUpdated => false',
            'public bool AuthorizedForPublication => false',
            'public bool SaveCompleted => false',
            'public bool DurableAcrossPowerLoss => false',
            'EuroOfficeSyntheticSnapshotJournal.RequireLastStage(attempt, "pending_flushed")',
            'EuroOfficeNativeHardLinkNoReplace.Create(destination, pending)',
            'EuroOfficeSyntheticSnapshotJournal.Append(attempt, "completed")',
        ):
            self.assertIn(marker,self.snapshot)

if __name__=="__main__":
    unittest.main()
