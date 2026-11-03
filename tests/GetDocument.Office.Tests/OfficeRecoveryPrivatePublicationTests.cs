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

    [TestMethod]
    public async Task ConsumerCopiesVerifiedBytesFromHeldHandleAndReleasesPublication()
    {
        using var scratch = new Scratch();
        using var session = OpenFakeSession(scratch.Source);
        var published = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        string copyPath = published.FilePath;
        using var output = new MemoryStream();

        RecoveryStagedCopyReceipt result =
            await published.ConsumeVerifiedToAsync(output);

        Assert.AreEqual((long)scratch.Bytes.Length, result.Length);
        Assert.AreEqual(scratch.Digest, result.Sha256);
        Assert.AreEqual(".docx", result.Extension);
        CollectionAssert.AreEqual(scratch.Bytes, output.ToArray());
        Assert.AreEqual(0L, output.Position);
        Assert.IsTrue(output.CanRead, "Consumer keeps its own output stream.");
        Assert.IsFalse(File.Exists(copyPath));
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task SecondConsumerIsRejectedWhileFirstTransferIsInProgress()
    {
        using var scratch = new Scratch();
        using var session = OpenFakeSession(scratch.Source);
        var published = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        using var blocked = new BlockFirstWriteStream();
        Task<RecoveryStagedCopyReceipt> first =
            published.ConsumeVerifiedToAsync(blocked);
        await blocked.FirstWriteStarted;
        using var second = new MemoryStream();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await published.ConsumeVerifiedToAsync(second));

        blocked.Release();
        RecoveryStagedCopyReceipt receipt = await first;
        Assert.AreEqual(scratch.Digest, receipt.Sha256);
        Assert.AreEqual(0L, second.Length);
        CollectionAssert.AreEqual(scratch.Bytes, blocked.ToArray());
    }

    [TestMethod]
    public async Task DisposalDuringTransferCancelsWithoutEarlyCleanup()
    {
        using var scratch = new Scratch();
        using var session = OpenFakeSession(scratch.Source);
        var published = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        string copyPath = published.FilePath;
        using var blocked = new BlockFirstWriteStream();
        Task<RecoveryStagedCopyReceipt> running =
            published.ConsumeVerifiedToAsync(blocked);
        await blocked.FirstWriteStarted;

        published.Dispose();
        // Publication disposal requests cancellation; active consumer owns
        // the handle until it can safely stop and perform cleanup.
        blocked.Release();
        // Depending on Stream/Task cancellation, .NET may throw the derived
        // TaskCanceledException rather than exactly OperationCanceledException.
        try
        {
            await running;
            Assert.Fail("Concurrent disposal must cancel the active transfer.");
        }
        catch (OperationCanceledException)
        {
            // Expected: no receipt and no usable partial consumer output.
        }

        Assert.AreEqual(0L, blocked.Length);
        Assert.IsFalse(File.Exists(copyPath));
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task CancellationImmediatelyAfterWriteInvalidatesTarget()
    {
        using var scratch = new Scratch();
        using var session = OpenFakeSession(scratch.Source);
        var published = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        using var cts = new CancellationTokenSource();
        using var target = new CancelOnFirstWriteStream(cts);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await published.ConsumeVerifiedToAsync(target, cts.Token));

        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.AreEqual(0L, target.Length);
        Assert.IsTrue(target.CanRead);
    }

    [TestMethod]
    public async Task NonemptyConsumerTargetIsNeverOverwrittenOrErased()
    {
        using var scratch = new Scratch();
        using var session = OpenFakeSession(scratch.Source);
        var published = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        byte[] foreign = System.Text.Encoding.UTF8.GetBytes("PRESERVE_CONSUMER_DATA");
        using var target = new MemoryStream();
        target.Write(foreign);
        target.Position = 0;

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await published.ConsumeVerifiedToAsync(target));

        CollectionAssert.AreEqual(foreign, target.ToArray());
        Assert.IsTrue(target.CanRead);
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task CorruptedConsumerBytesFailIndependentShaZipAndClearTarget()
    {
        using var scratch = new Scratch();
        using var session = OpenFakeSession(scratch.Source);
        var published = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        using var target = new CorruptingWriteStream();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await published.ConsumeVerifiedToAsync(target));

        Assert.AreEqual(0L, target.Length);
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task VerifiedPublicationCannotBeConsumedTwice()
    {
        using var scratch = new Scratch();
        using var session = OpenFakeSession(scratch.Source);
        var published = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        using var first = new MemoryStream();
        await published.ConsumeVerifiedToAsync(first);
        using var second = new MemoryStream();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await published.ConsumeVerifiedToAsync(second));

        Assert.AreEqual(0L, second.Length);
    }

    [TestMethod]
    public async Task SwappedPathDoesNotChangeHeldSourceBytesOrDeleteForeignReplacement()
    {
        using var scratch = new Scratch();
        using var session = OpenFakeSession(scratch.Source);
        var published = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        string oldPath = published.FilePath;
        string folder = Path.GetDirectoryName(oldPath)!;
        string moved = Path.Combine(folder, "moved-original.docx");
        byte[] unrelated = System.Text.Encoding.UTF8.GetBytes("FOREIGN_AFTER_RENAME");

        File.Move(oldPath, moved);
        File.WriteAllBytes(oldPath, unrelated);
        using var output = new MemoryStream();
        RecoveryStagedCopyReceipt result =
            await published.ConsumeVerifiedToAsync(output);

        Assert.AreEqual(scratch.Digest, result.Sha256);
        CollectionAssert.AreEqual(scratch.Bytes, output.ToArray());
        CollectionAssert.AreEqual(unrelated, File.ReadAllBytes(oldPath),
            "Consumer disposal must not delete a changed destination name.");
        File.Delete(oldPath);
        File.Delete(moved);
        Directory.Delete(folder);
    }

    private sealed class BlockFirstWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _resume =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstWriteStarted => _started.Task;
        public void Release() => _resume.TrySetResult(true);
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult(true);
            await _resume.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class CancelOnFirstWriteStream : MemoryStream
    {
        private readonly CancellationTokenSource _cancel;
        public CancelOnFirstWriteStream(CancellationTokenSource cancel) => _cancel = cancel;
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            _cancel.Cancel();
        }
    }

    private sealed class CorruptingWriteStream : MemoryStream
    {
        private bool _corrupted;
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (_corrupted || Length == 0) return;
            _corrupted = true;
            long oldPosition = Position;
            Position = 0;
            WriteByte(0); // damage ZIP signature in consumer-owned bytes
            Position = oldPosition;
        }
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
