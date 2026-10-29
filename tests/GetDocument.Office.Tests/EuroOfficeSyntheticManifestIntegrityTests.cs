using System.Text.Json.Nodes;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSyntheticManifestIntegrityTests
{
    [DataTestMethod]
    [DataRow("docx")]
    [DataRow("xlsx")]
    [DataRow("pptx")]
    public void AllThreeSyntheticFormatPackagesAndManifestMustMatch(string selectedFormat)
    {
        using var lab = new EuroOfficeSyntheticReviewedSnapshotTests.SyntheticLab(selectedFormat);
        lab.Create();
        var review = lab.GetReview();
        Assert.AreEqual(selectedFormat, review.Candidate!.Format);
        string originalSha = lab.Hash(lab.Source);
        string candidateSha = lab.Hash(lab.Candidate);
        var receipt = EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true);
        Assert.AreEqual(candidateSha, receipt.Sha256);
        Assert.AreEqual(originalSha, lab.Hash(lab.Source));
        Assert.AreEqual(candidateSha, lab.Hash(lab.Candidate));
        Assert.AreEqual(EuroOfficeSyntheticSnapshotJournal.Observation.CompleteObserved,
            EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, true).Single().State);
        Assert.IsFalse(receipt.AuthorizedForPublication);
        Assert.IsFalse(receipt.DurableAcrossPowerLoss);
    }

    [TestMethod]
    public void UntouchedSelectedDocxCannotIgnoreModifiedSiblingXlsx()
    {
        using var lab = new EuroOfficeSyntheticReviewedSnapshotTests.SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        File.AppendAllText(Path.Combine(lab.Root, "gd31j6_calc.xlsx"), "tampered sibling");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void UntouchedSelectedXlsxCannotIgnoreDeletedSiblingPresentation()
    {
        using var lab = new EuroOfficeSyntheticReviewedSnapshotTests.SyntheticLab("xlsx");
        lab.Create();
        var review = lab.GetReview();
        File.Delete(Path.Combine(lab.Root, "gd31j6_impress.pptx"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void ManifestWithThreeEntriesCannotHideForeignNameOrDuplicateKey()
    {
        using var lab = new EuroOfficeSyntheticReviewedSnapshotTests.SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        string pristine = File.ReadAllText(lab.Manifest);
        File.WriteAllText(lab.Manifest,
            pristine.Replace("\"gd31j6_calc.xlsx\":", "\"foreign_calc.xlsx\":", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        File.WriteAllText(lab.Manifest,
            pristine.Replace("\"gd31j6_calc.xlsx\":", "\"gd31j6_writer.docx\":", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void IncorrectHashOrSizeForNonSelectedManifestEntryCannotBeIgnored()
    {
        using var lab = new EuroOfficeSyntheticReviewedSnapshotTests.SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        string pristine = File.ReadAllText(lab.Manifest);
        JsonNode wrongHash = JsonNode.Parse(pristine)!;
        wrongHash["files"]!["gd31j6_calc.xlsx"]!["sha256"] = new string('0', 64);
        File.WriteAllText(lab.Manifest, wrongHash.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        JsonNode wrongSize = JsonNode.Parse(pristine)!;
        wrongSize["files"]!["gd31j6_impress.pptx"]!["size"] = 1;
        File.WriteAllText(lab.Manifest, wrongSize.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }
}
