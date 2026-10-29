using System.IO.Compression;
using System.Security.Cryptography;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>Offline synthetic-only staged Save As tests; never access a real Office installation.</summary>
[TestClass]
public sealed class EuroOfficeStagedSaveTransactionTests
{
    [TestMethod]
    public async Task PublishesOnlyNewValidatedLocalRevisionAndKeepsOriginalUnchanged()
    {
        using var fixture = new Fixture();
        byte[] before = await File.ReadAllBytesAsync(fixture.Source);
        var transaction = fixture.Prepare();
        fixture.CreateWord(fixture.Candidate, "updated text");
        var result = await transaction.PublishAsync(fixture.Session, fixture.Tab, CancellationToken.None);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Source));
        Assert.IsTrue(File.Exists(fixture.Published));
        CollectionAssert.AreEqual(
            await File.ReadAllBytesAsync(fixture.Candidate),
            await File.ReadAllBytesAsync(fixture.Published));
        Assert.AreEqual("docx", result.Format);
        Assert.AreEqual(fixture.Published, result.LocalPath);
        Assert.AreEqual(64, result.Sha256.Length);
        Assert.IsFalse(result.WormCommitted);
        Assert.IsFalse(result.AuthorizedForCorporatePublication);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            transaction.PublishAsync(fixture.Session, fixture.Tab, CancellationToken.None)
                .GetAwaiter().GetResult());
    }

    [TestMethod]
    public async Task RefusesChangedSourceWithoutCreatingPublishedFile()
    {
        using var fixture = new Fixture();
        var transaction = fixture.Prepare();
        fixture.CreateWord(fixture.Candidate, "changed");
        fixture.CreateWord(fixture.Source, "tampered source");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            transaction.PublishAsync(fixture.Session, fixture.Tab, CancellationToken.None));
        Assert.IsFalse(File.Exists(fixture.Published));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Root, ".getdoc-save-*").Any());
    }

    [TestMethod]
    public async Task RefusesInvalidNativeCandidateAndLeavesOriginalAndDestinationUntouched()
    {
        using var fixture = new Fixture();
        var transaction = fixture.Prepare();
        using (var archive = new ZipArchive(File.Create(fixture.Candidate), ZipArchiveMode.Create))
        {
            Fixture.Add(archive, "[Content_Types].xml", "<Types/>");
            Fixture.Add(archive, "word/document.xml", "<w:document/>");
            Fixture.Add(archive, "word/vbaProject.bin", "macro");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            transaction.PublishAsync(fixture.Session, fixture.Tab, CancellationToken.None));
        Assert.IsFalse(File.Exists(fixture.Published));
    }

    [TestMethod]
    public async Task DestinationCreatedAfterPrepareIsNeverOverwritten()
    {
        using var fixture = new Fixture();
        var transaction = fixture.Prepare();
        fixture.CreateWord(fixture.Candidate, "updated");
        byte[] existing = "reserved output"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.Published, existing);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            transaction.PublishAsync(fixture.Session, fixture.Tab, CancellationToken.None));
        CollectionAssert.AreEqual(existing, await File.ReadAllBytesAsync(fixture.Published));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Root, ".getdoc-save-*").Any());
    }

    [TestMethod]
    public async Task SessionAndTabMustMatchAndRejectionsDoNotConsumeTicket()
    {
        using var fixture = new Fixture();
        var transaction = fixture.Prepare();
        fixture.CreateWord(fixture.Candidate, "updated");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            transaction.PublishAsync(Guid.NewGuid(), fixture.Tab, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            transaction.PublishAsync(fixture.Session, Guid.NewGuid(), CancellationToken.None));
        var result = await transaction.PublishAsync(fixture.Session, fixture.Tab, CancellationToken.None);
        Assert.IsTrue(File.Exists(result.LocalPath));
    }

    [TestMethod]
    public async Task NotApprovedOrNonSeparatePathsAreRejectedBeforeAnyWrite()
    {
        using var fixture = new Fixture();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            fixture.Prepare(labApproved: false));
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeStagedSaveTransaction.Prepare(fixture.Root, fixture.Source,
                fixture.Candidate, fixture.Candidate, fixture.Session, fixture.Tab,
                labApproved: true));
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeStagedSaveTransaction.Prepare(fixture.Root, fixture.Source,
                fixture.Source, fixture.Published, fixture.Session, fixture.Tab,
                labApproved: true));
        Assert.IsFalse(File.Exists(fixture.Published));
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task CancelledAttemptCreatesNoDestination()
    {
        using var fixture = new Fixture();
        var transaction = fixture.Prepare();
        fixture.CreateWord(fixture.Candidate, "updated");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            transaction.PublishAsync(fixture.Session, fixture.Tab, cts.Token));
        Assert.IsFalse(File.Exists(fixture.Published));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "gd31j20-save-" + Guid.NewGuid().ToString("N"));
        public Guid Session { get; } = Guid.NewGuid();
        public Guid Tab { get; } = Guid.NewGuid();
        public string Source => Path.Combine(Root, "original.docx");
        public string Candidate => Path.Combine(Root, "native-candidate.docx");
        public string Published => Path.Combine(Root, "local-revision.docx");

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            CreateWord(Source, "original text");
        }

        public EuroOfficeStagedSaveTransaction Prepare(bool labApproved = true) =>
            EuroOfficeStagedSaveTransaction.Prepare(Root, Source, Candidate, Published,
                Session, Tab, labApproved);

        public void CreateWord(string path, string text)
        {
            using var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
            Add(archive, "[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add(archive, "word/document.xml",
                "<w:document xmlns:w=\"urn:test\"><w:t>" + text + "</w:t></w:document>");
        }

        public static void Add(ZipArchive archive, string name, string data)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(data);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
