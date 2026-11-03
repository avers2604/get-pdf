using System.IO.Compression;
using System.Security.Cryptography;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bg: synthetic isolated workspace tests. Fake identity does not
/// prove native Windows/Astra safety. No user documents or WORM objects.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryPrivatePublicationTests
{
    [TestMethod]
    public async Task ValidStageIsPublishedWithoutLeavingPartFileAndOwnedUntilDispose()
    {
        using var scratch = new Scratch();
        string? directory = null;
        using var session = OpenFakeSession(scratch.Source);
        RecoveryPrivatePublication publication =
            await RecoveryPrivateCopyPublisher.PublishAsync(
                session, scratch.Digest, ".docx",
                onPrivateDirectoryCreated: value => directory = value);

        Assert.IsNotNull(directory);
        Assert.AreEqual(directory, Path.GetDirectoryName(publication.FilePath));
        Assert.IsTrue(File.Exists(publication.FilePath));
        Assert.AreEqual(scratch.Digest, publication.Receipt.Sha256);
        Assert.AreEqual((long)scratch.Bytes.Length, publication.Receipt.Length);
        Assert.AreEqual(".docx", publication.Receipt.Extension);
        Assert.AreEqual(1, Directory.GetFileSystemEntries(directory).Length);
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(publication.FilePath));

        publication.Dispose();
        Assert.IsFalse(Directory.Exists(directory));
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task WrongDigestDiscardsOnlyOwnedUnpublishedStage()
    {
        using var scratch = new Scratch();
        string? directory = null;
        using var session = OpenFakeSession(scratch.Source);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await RecoveryPrivateCopyPublisher.PublishAsync(
                session, new string('0', 64), ".docx",
                onPrivateDirectoryCreated: value => directory = value));

        Assert.IsNotNull(directory);
        Assert.IsFalse(Directory.Exists(directory));
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task ExistingDestinationCollisionMustNotReplaceForeignContents()
    {
        using var scratch = new Scratch();
        string? foreign = null;
        string? directory = null;
        using var session = OpenFakeSession(scratch.Source);
        byte[] sentinel = System.Text.Encoding.UTF8.GetBytes("FOREIGN_PRESERVE");

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await RecoveryPrivateCopyPublisher.PublishAsync(
                session, scratch.Digest, ".docx",
                onPrivateDirectoryCreated: value => directory = value,
                beforeRename: destination =>
                {
                    foreign = destination;
                    File.WriteAllBytes(destination, sentinel);
                }));

        Assert.IsNotNull(foreign);
        CollectionAssert.AreEqual(sentinel, File.ReadAllBytes(foreign));
        Assert.IsFalse(Directory.GetFileSystemEntries(directory!, "*.part.docx").Any());
        File.Delete(foreign);
        Directory.Delete(directory!);
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task CancellationBeforeRenameNeverPublishesFile()
    {
        using var scratch = new Scratch();
        using var cts = new CancellationTokenSource();
        string? directory = null;
        using var session = OpenFakeSession(scratch.Source);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await RecoveryPrivateCopyPublisher.PublishAsync(
                session, scratch.Digest, ".docx", cts.Token,
                onPrivateDirectoryCreated: value => directory = value,
                beforeRename: _ => cts.Cancel()));

        Assert.IsNotNull(directory);
        Assert.IsFalse(Directory.Exists(directory));
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task ExistingSymbolicLinkAtDestinationCannotBeOverwritten()
    {
        using var scratch = new Scratch();
        string? link = null;
        string? directory = null;
        using var session = OpenFakeSession(scratch.Source);

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await RecoveryPrivateCopyPublisher.PublishAsync(
                session, scratch.Digest, ".docx",
                onPrivateDirectoryCreated: value => directory = value,
                beforeRename: destination =>
                {
                    link = destination;
                    File.CreateSymbolicLink(destination, scratch.Source);
                }));

        Assert.IsNotNull(link);
        Assert.IsNotNull(new FileInfo(link).LinkTarget);
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
        File.Delete(link);
        Directory.Delete(directory!);
    }

    [TestMethod]
    public async Task InvalidExtensionDoesNotCreatePrivateDirectory()
    {
        using var scratch = new Scratch();
        bool created = false;
        using var session = OpenFakeSession(scratch.Source);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await RecoveryPrivateCopyPublisher.PublishAsync(
                session, scratch.Digest, ".json",
                onPrivateDirectoryCreated: _ => created = true));
        Assert.IsFalse(created);
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task DisposingPublicationCannotDeleteUnrelatedNewFiles()
    {
        using var scratch = new Scratch();
        using var session = OpenFakeSession(scratch.Source);
        var publication = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        string folder = Path.GetDirectoryName(publication.FilePath)!;
        string unrelated = Path.Combine(folder, "FOREIGN-PRESERVE.txt");
        File.WriteAllText(unrelated, "keep");

        publication.Dispose();
        Assert.IsTrue(File.Exists(unrelated));
        Assert.IsFalse(File.Exists(publication.FilePath));
        Assert.IsTrue(Directory.Exists(folder));
        File.Delete(unrelated);
        Directory.Delete(folder);
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    private static RecoveryVerifiedStreamStage OpenFakeSession(string file)
    {
        var adapter = new FakeAdapter(file);
        return RecoveryVerifiedStreamStage.Open(
            adapter, RecoveryRelativeKey.Create(
                new string('C', 64), Guid.NewGuid().ToString("N") + ".docx"));
    }

    private sealed class FakeAdapter : IRecoveryNativeIdentityAdapter
    {
        private readonly string _path;
        public FakeAdapter(string path) => _path = path;
        public RecoveryNativePlatform Platform => RecoveryNativePlatform.Windows;
        public bool SupportsVerifiedDirectoryRelativeOpen => true;

        public RecoveryNativeReadLease OpenReadOnlyRelative(RecoveryRelativeKey key)
        {
            var parent = new RecoveryNativeFileId(RecoveryNativePlatform.Windows, 10, 20, 30);
            var file = new RecoveryNativeFileId(RecoveryNativePlatform.Windows, 10, 40, 50);
            return new RecoveryNativeReadLease(
                new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read),
                RecoveryNativePlatform.Windows, parent, parent, file, file,
                directoryRelative: true, noFollow: true);
        }
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bg-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(_dir, "synthetic.docx");
        public byte[] Bytes => File.ReadAllBytes(Source);
        public string Digest => Convert.ToHexString(SHA256.HashData(Bytes));

        public Scratch()
        {
            Directory.CreateDirectory(_dir);
            using var output = File.Create(Source);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            using (var entry = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                entry.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var entry = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                entry.Write("<root/>");
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
    }
}
