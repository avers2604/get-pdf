"""GD-31J.35 offline source contract: honest three-format synthetic fixture suite.

This verifies source structure without executing .NET, creating OOXML files,
performing ZIP parsing or asserting Microsoft Office compatibility.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"
TESTS=ROOT/"tests/GetDocument.Office.Tests"

class SyntheticFixtureManifestIntegrityContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.snapshot=(OFFICE/"EuroOfficeSyntheticReviewedSnapshot.cs").read_text(encoding="utf-8")
        cls.fixture=(TESTS/"EuroOfficeSyntheticReviewedSnapshotTests.cs").read_text(encoding="utf-8")
        cls.regressions=(TESTS/"EuroOfficeSyntheticManifestIntegrityTests.cs").read_text(encoding="utf-8")

    def test_all_three_manifest_members_are_verified_before_any_pending_file(self):
        src=self.snapshot
        for item in (
            "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx",
            "new HashSet<string>(StringComparer.Ordinal)",
            "foreach (JsonProperty property in files.EnumerateObject())",
            "!expectedNames.Contains(property.Name)",
            "!seen.Add(property.Name)",
            'field.Name is not ("size" or "sha256")',
            "if (entryFields.Count != 2",
            "string actualHash = HashBounded(path, out long actualLength)",
            "actualLength != expectedLength || actualHash != declaredHash",
            "seen.Count != expectedNames.Count",
        ):
            self.assertIn(item,src)
        self.assertLess(src.index("RequireManifest(manifest, native.Core.FixtureName,"),
                        src.index("EuroOfficeSyntheticSnapshotJournal.Begin("))
        self.assertLess(src.index("RequireManifest(manifest, native.Core.FixtureName,"),
                        src.index("FileMode.CreateNew",src.index("EuroOfficeSyntheticSnapshotJournal.Begin(")))

    def test_fixture_is_physically_present_and_uses_real_hashes_not_placeholders(self):
        src=self.fixture
        self.assertIn('internal sealed class SyntheticLab',src)
        self.assertIn('public SyntheticLab(string format = "docx")',src)
        self.assertIn('foreach (string name in names)',src)
        self.assertIn('WriteOoxml(path, "Synthetic original " + name)',src)
        self.assertIn('records.Add(name, new { size = new FileInfo(path).Length, sha256 = Hash(path) })',src)
        self.assertIn('WriteOoxml(Candidate, "Synthetic edited " + Format)',src)
        self.assertIn('"_rels/.rels"',src)
        for part in ("word/document.xml","xl/workbook.xml",
                     "xl/worksheets/sheet1.xml","ppt/presentation.xml",
                     "ppt/slides/slide1.xml"):
            self.assertIn(part,src)
        self.assertNotIn("new string('a', 64)",src)

    def test_cross_format_and_tamper_regressions_are_declared(self):
        for name in (
            'AllThreeSyntheticFormatPackagesAndManifestMustMatch',
            'UntouchedSelectedDocxCannotIgnoreModifiedSiblingXlsx',
            'UntouchedSelectedXlsxCannotIgnoreDeletedSiblingPresentation',
            'ManifestWithThreeEntriesCannotHideForeignNameOrDuplicateKey',
            'IncorrectHashOrSizeForNonSelectedManifestEntryCannotBeIgnored'
        ):
            self.assertIn(name,self.regressions)
        for fmt in ('[DataRow("docx")]','[DataRow("xlsx")]','[DataRow("pptx")]'):
            self.assertIn(fmt,self.regressions)
        self.assertIn("Assert.IsFalse(receipt.AuthorizedForPublication)",self.regressions)
        self.assertIn("Assert.IsFalse(receipt.DurableAcrossPowerLoss)",self.regressions)

if __name__=="__main__":
    unittest.main()
