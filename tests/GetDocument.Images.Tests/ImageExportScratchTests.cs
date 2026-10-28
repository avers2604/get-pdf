using System.Text;
using GetDocument.Images;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Images.Tests;

[TestClass]
public sealed class ImageExportScratchTests
{
    [DataTestMethod]
    [DataRow(".png", "stage.png")]
    [DataRow(".jpg", "stage.jpg")]
    public void FinishedExportDeletesOnlyItsScratchAndPreservesPublishedDocument(
        string extension, string stagedName)
    {
        using var fixture = new Fixture();
        string userDocument = Path.Combine(fixture.Path, "contract.pdf");
        File.WriteAllText(userDocument, "important");
        string destination = Path.Combine(fixture.Path, "new" + extension);
        string scratchFolder;
        using (var scratch = ImageExportScratch.Create(fixture.Path, extension))
        {
            scratchFolder = scratch.DirectoryPath;
            Assert.AreEqual(stagedName, Path.GetFileName(scratch.StagingPath));
            File.WriteAllText(scratch.StagingPath, "new image bytes");
            File.Move(scratch.StagingPath, destination, overwrite: false);
        }

        Assert.IsFalse(Directory.Exists(scratchFolder));
        Assert.AreEqual("new image bytes", File.ReadAllText(destination));
        Assert.AreEqual("important", File.ReadAllText(userDocument));
    }

    [DataTestMethod]
    [DataRow(".png")]
    [DataRow(".jpg")]
    public void OldOwnedScratchIsReclaimedOnNextExport(string extension)
    {
        using var fixture = new Fixture();
        string orphan = fixture.MakeAbandonedScratch(extension);
        string source = Path.Combine(fixture.Path, "original.png");
        string existingOutput = Path.Combine(fixture.Path, "already-saved.jpg");
        File.WriteAllText(source, "source");
        File.WriteAllText(existingOutput, "saved image");

        using var next = ImageExportScratch.Create(fixture.Path, extension);

        Assert.IsFalse(Directory.Exists(orphan));
        Assert.AreEqual("source", File.ReadAllText(source));
        Assert.AreEqual("saved image", File.ReadAllText(existingOutput));
        Assert.IsTrue(Directory.Exists(next.DirectoryPath));
    }

    [TestMethod]
    public void CrashAfterPublishReclaimsOnlyEmptyOwnedFolder()
    {
        using var fixture = new Fixture();
        string oldScratch = fixture.MakeAbandonedScratch(".png");
        string published = Path.Combine(fixture.Path, "published.png");
        File.Move(Path.Combine(oldScratch, "stage.png"), published, overwrite: false);

        ImageExportScratch.CleanupAbandoned(fixture.Path, TimeSpan.FromHours(24));

        Assert.IsFalse(Directory.Exists(oldScratch));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(published));
    }

    [TestMethod]
    public void ActiveLeaseCannotBeReclaimedEvenWithZeroAge()
    {
        using var fixture = new Fixture();
        using var active = ImageExportScratch.Create(fixture.Path, ".png");
        File.WriteAllBytes(active.StagingPath, [1, 2, 3]);
        ImageExportScratch.CleanupAbandoned(fixture.Path, TimeSpan.Zero);

        Assert.IsTrue(File.Exists(active.StagingPath));
    }

    [TestMethod]
    public void RecentlyInterruptedScratchRemainsDuringSafetyWindow()
    {
        using var fixture = new Fixture();
        string folder = fixture.MakeAbandonedScratch(".png");
        string marker = Path.Combine(folder, ImageExportScratch.LeaseFileName);
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);

        ImageExportScratch.CleanupAbandoned(fixture.Path, TimeSpan.FromHours(24));

        Assert.IsTrue(File.Exists(Path.Combine(folder, "stage.png")));
    }

    [TestMethod]
    public void RecentlyModifiedStageSurvivesEvenWithAnOldOwnerMarker()
    {
        using var fixture = new Fixture();
        string orphan = fixture.MakeAbandonedScratch(".png");
        string staged = Path.Combine(orphan, "stage.png");
        File.SetLastWriteTimeUtc(staged, DateTime.UtcNow);

        ImageExportScratch.CleanupAbandoned(fixture.Path, TimeSpan.FromHours(24));

        Assert.IsTrue(File.Exists(staged));
        Assert.IsTrue(Directory.Exists(orphan));
    }

    [TestMethod]
    public void TwoStagingImagesBlockDeletion()
    {
        using var fixture = new Fixture();
        string orphan = fixture.MakeAbandonedScratch(".jpg");
        string other = Path.Combine(orphan, "stage.png");
        File.WriteAllText(other, "unrelated");
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-3));

        ImageExportScratch.CleanupAbandoned(fixture.Path, TimeSpan.FromHours(24));

        Assert.IsTrue(File.Exists(Path.Combine(orphan, "stage.jpg")));
        Assert.AreEqual("unrelated", File.ReadAllText(other));
    }

    [TestMethod]
    public void ForeignFilesInsideOwnedScratchBlockCleanup()
    {
        using var fixture = new Fixture();
        string folder = fixture.MakeAbandonedScratch(".png");
        string important = Path.Combine(folder, "invoice.docx");
        File.WriteAllText(important, "do not delete");

        ImageExportScratch.CleanupAbandoned(fixture.Path, TimeSpan.FromHours(24));

        Assert.IsTrue(File.Exists(important));
        Assert.IsTrue(File.Exists(Path.Combine(folder, "stage.png")));
    }

    [TestMethod]
    public void InvalidOwnerMarkerAndNonGuidNameAreNeverReclaimed()
    {
        using var fixture = new Fixture();
        string invalid = fixture.MakeAbandonedScratch(".png");
        string invalidMarker = Path.Combine(invalid, ImageExportScratch.LeaseFileName);
        File.WriteAllText(invalidMarker, "not owner");
        File.SetLastWriteTimeUtc(invalidMarker, DateTime.UtcNow.AddDays(-3));
        string lookalike = Path.Combine(fixture.Path, ".get-document-image-important");
        Directory.CreateDirectory(lookalike);
        File.WriteAllText(Path.Combine(lookalike, ImageExportScratch.LeaseFileName),
            ImageExportScratch.LeaseMarker, Encoding.ASCII);
        string document = Path.Combine(lookalike, "stage.png");
        File.WriteAllText(document, "keep");
        File.SetLastWriteTimeUtc(Path.Combine(lookalike, ImageExportScratch.LeaseFileName),
            DateTime.UtcNow.AddDays(-3));

        ImageExportScratch.CleanupAbandoned(fixture.Path, TimeSpan.FromHours(24));

        Assert.IsTrue(Directory.Exists(invalid));
        Assert.AreEqual("keep", File.ReadAllText(document));
    }

    [TestMethod]
    public void LegacyLooseTempFileAndUnexpectedSubdirectoryAreNotDeleted()
    {
        using var fixture = new Fixture();
        string loose = Path.Combine(fixture.Path, ".get-image-" +
            Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(loose, "legacy data");
        string folder = fixture.MakeAbandonedScratch(".jpg");
        Directory.CreateDirectory(Path.Combine(folder, "notes"));

        ImageExportScratch.CleanupAbandoned(fixture.Path, TimeSpan.FromHours(24));

        Assert.AreEqual("legacy data", File.ReadAllText(loose));
        Assert.IsTrue(Directory.Exists(folder));
    }

    [TestMethod]
    public void UnsupportedExtensionIsRejectedBeforeScratchCreation()
    {
        using var fixture = new Fixture();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ImageExportScratch.Create(fixture.Path, ".pdf"));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Path).Length);
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gd-image-crash-" + Guid.NewGuid().ToString("N"));

        public Fixture() => Directory.CreateDirectory(Path);

        public string MakeAbandonedScratch(string extension)
        {
            string directory = System.IO.Path.Combine(Path,
                ".get-document-image-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string marker = System.IO.Path.Combine(directory, ImageExportScratch.LeaseFileName);
            File.WriteAllText(marker, ImageExportScratch.LeaseMarker, Encoding.ASCII);
            string staged = System.IO.Path.Combine(directory,
                extension == ".png" ? "stage.png" : "stage.jpg");
            File.WriteAllBytes(staged, [4, 5, 6]);
            DateTime abandoned = DateTime.UtcNow.AddDays(-3);
            File.SetLastWriteTimeUtc(staged, abandoned);
            File.SetLastWriteTimeUtc(marker, abandoned);
            return directory;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
