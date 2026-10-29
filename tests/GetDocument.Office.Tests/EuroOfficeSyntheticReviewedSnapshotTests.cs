using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSyntheticReviewedSnapshotTests
{
    private static readonly Guid Session =
        Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab =
        Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";

    [TestMethod]
    public void CommitCreatesNewReviewedSnapshotWithoutMutatingSourceOrQuarantine()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        string original = lab.Hash(lab.Source);
        string quarantine = lab.Hash(lab.Candidate);
        var saved = EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, labApproved: true);
        Assert.IsTrue(saved.SyntheticOnly);
        Assert.IsFalse(saved.AuthorizedForPublication);
        Assert.IsFalse(saved.WormUpdated);
        Assert.AreEqual(EuroOfficeSyntheticReviewedSnapshot.ExpectedName(Session, "docx"), saved.FileName);
        Assert.AreEqual(quarantine, saved.Sha256);
        Assert.AreEqual(quarantine, lab.Hash(Path.Combine(lab.Reviewed, saved.FileName)));
        Assert.AreEqual(original, lab.Hash(lab.Source));
        Assert.AreEqual(quarantine, lab.Hash(lab.Candidate));
        Assert.IsTrue(Directory.GetFiles(lab.Reviewed, "*.tmp").Length == 0);
        var journal = EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, labApproved: true);
        Assert.AreEqual(1, journal.Count);
        Assert.AreEqual(EuroOfficeSyntheticSnapshotJournal.Observation.CompleteObserved,
            journal[0].State);
        Assert.AreEqual("completed", journal[0].LastStage);
        Assert.IsFalse(journal[0].TrustedForDeletion);
    }

    [TestMethod]
    public void SecondCommitCannotOverwriteExistingReviewedFile()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        _ = EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true);
        string destination = Path.Combine(lab.Reviewed,
            EuroOfficeSyntheticReviewedSnapshot.ExpectedName(Session, "docx"));
        string before = lab.Hash(destination);
        Assert.ThrowsExactly<IOException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(before, lab.Hash(destination));
    }

    [TestMethod]
    public void RefusesApprovalByDefaultOrWithoutAuthenticCorrelatedNativeEvidence()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, false));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root,
                review with { NativeEvidence = null }, true));
        var broken = review.NativeEvidence! with { FailureCode = "timeout" };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root,
                review with { NativeEvidence = broken }, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void SourceMutationOrCandidateMutationFailsBeforeReviewedSnapshot()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        File.AppendAllText(lab.Candidate, "damaged");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        lab.Create();
        review = lab.GetReview();
        File.AppendAllText(lab.Source, "different");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void MissingOrUnapprovedManifestPreventsAnyOutput()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        File.Delete(lab.Manifest);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void RejectsLinkedReviewedFolder()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        string linked = lab.Reviewed;
        Directory.Delete(linked);
        try
        {
            Directory.CreateSymbolicLink(linked, lab.Root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                   or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Символические ссылки запрещены платформой стенда.");
            return;
        }
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
    }

    internal sealed class SyntheticLab : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "gd31j29-synth-" + Guid.NewGuid().ToString("N"));
        public string Reviewed => Path.Combine(Root, "reviewed");
        public string Source => Path.Combine(Root, Fixture);
        public string Candidate => Path.Combine(Root,
            EuroOfficeNativeSaveAsReceipt.CandidateNameFor(Session, "docx"));
        public string Manifest => Path.Combine(Root, "gd31j6-fixture-manifest.json");

        public SyntheticLab()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Reviewed);
        }

        public string Hash(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        public void Create()
        {
            WriteOoxml(Source, "Original synthetic");
            WriteOoxml(Candidate, "Edited synthetic");
            var originals = new Dictionary<string, object>
            {
                [Fixture] = new { size = new FileInfo(Source).Length, sha256 = Hash(Source) },
                ["gd31j6_calc.xlsx"] = new { size = 1L, sha256 = new string('a', 64) },
                ["gd31j6_impress.pptx"] = new { size = 1L, sha256 = new string('a', 64) }
            };
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new
            {
                schema = "gd31j6-office-fixtures/1",
                purpose = "test-only-local-ooxml-no-corporate-documents",
                files = originals
            }));
        }

        public EuroOfficeNativeSaveAsLab.Review GetReview()
        {
            string oldHash = Hash(Source);
            string newHash = Hash(Candidate);
            var core = new EuroOfficeNativeSaveAsReceipt(
                Session, Tab, Fixture, "docx", oldHash, oldHash,
                true, true, true, false, true,
                Path.GetFileName(Candidate), new FileInfo(Candidate).Length, newHash);
            var native = new EuroOfficeNativeSaveAsEventReceipt(core,
                "candidate_untrusted", "quarantined", 1, 1, 1, 2);
            return EuroOfficeNativeSaveAsLab.ReviewCandidate(
                Root, Fixture, core, native);
        }

        private static void WriteOoxml(string path, string value)
        {
            if (File.Exists(path)) File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var types = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                types.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var document = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                document.Write("<document><t>" + value + "</t></document>");
        }

        public void Dispose()
        {
            // Only this disposable test-created directory. The production
            // synthetic snapshot code never deletes originals or snapshots.
            if (Directory.Exists(Reviewed) &&
                (File.GetAttributes(Reviewed) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(Reviewed);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
