using System.IO.Compression;
using System.Security.Cryptography;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bi: synthetic file-backed consumer ownership tests.
/// Fake native identity does not prove platform security. No user files/WORM.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryOwnedConsumerOutputTests
{
    [TestMethod]
    public async Task VerifiedFileBackedConsumerGetsReadOnlyBytesWithNoPath()
    {
        using var scratch = new Scratch();
        string? directory = null;
        using var owned = await scratch.CreateOwnedAsync(
            onPrivateDirectoryCreated: path => directory = path);

        Assert.IsNotNull(directory);
        Assert.IsTrue(Directory.Exists(directory));
        byte[] observed = await owned.ReadVerifiedAsync(async (view, cancel) =>
        {
            Assert.IsTrue(view.CanRead);
            Assert.IsTrue(view.CanSeek);
            Assert.IsFalse(view.CanWrite);
            Assert.IsFalse(view is FileStream);
            Assert.ThrowsExactly<NotSupportedException>(() => view.WriteByte(9));
            using var copy = new MemoryStream();
            await view.CopyToAsync(copy, cancel);
            return copy.ToArray();
        });

        CollectionAssert.AreEqual(scratch.Bytes, observed);
        Assert.IsFalse(Directory.Exists(directory),
            "Completion must close the DeleteOnClose file and remove empty folder.");
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task ConsumerViewIsRevokedImmediatelyAfterCallback()
    {
        using var scratch = new Scratch();
        using var owned = await scratch.CreateOwnedAsync();
        Stream? leaked = null;

        int firstByte = await owned.ReadVerifiedAsync((view, _) =>
        {
            leaked = view;
            return Task.FromResult(view.ReadByte());
        });

        Assert.AreEqual((int)'P', firstByte);
        Assert.IsNotNull(leaked);
        Assert.ThrowsExactly<ObjectDisposedException>(() => leaked.ReadByte());
    }

    [TestMethod]
    public async Task ASecondReaderCannotOverlapFirstOrReuseConsumedOutput()
    {
        using var scratch = new Scratch();
        using var owned = await scratch.CreateOwnedAsync();
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = owned.ReadVerifiedAsync(async (view, ct) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return view.ReadByte();
        });
        await entered.Task;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            owned.ReadVerifiedAsync((view, ct) => Task.FromResult(view.ReadByte())));
        release.TrySetResult(true);
        Assert.AreEqual((int)'P', await first);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            owned.ReadVerifiedAsync((view, ct) => Task.FromResult(view.ReadByte())));
    }

    [TestMethod]
    public async Task DisposeDuringCallbackCancelsAndWaitsForCallbackExitToCleanup()
    {
        using var scratch = new Scratch();
        string? directory = null;
        var owned = await scratch.CreateOwnedAsync(
            onPrivateDirectoryCreated: path => directory = path);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> running = owned.ReadVerifiedAsync(async (view, ct) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            ct.ThrowIfCancellationRequested();
            return view.ReadByte();
        });
        await entered.Task;
        Assert.IsTrue(Directory.Exists(directory));

        owned.Dispose();
        Assert.IsTrue(Directory.Exists(directory),
            "An active consumer still holds the descriptor during cancellation.");
        release.TrySetResult(true);
        try
        {
            await running;
            Assert.Fail("Disposal during callback must not return verified data.");
        }
        catch (OperationCanceledException) { }

        Assert.IsFalse(Directory.Exists(directory));
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task ConsumerCallbackExceptionRevokesViewAndCleansOwnedFile()
    {
        using var scratch = new Scratch();
        string? directory = null;
        using var owned = await scratch.CreateOwnedAsync(
            onPrivateDirectoryCreated: path => directory = path);
        Stream? leaked = null;

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            owned.ReadVerifiedAsync<int>((view, _) =>
            {
                leaked = view;
                throw new IOException("Synthetic consumer exception");
            }));

        Assert.IsNotNull(leaked);
        Assert.ThrowsExactly<ObjectDisposedException>(() => leaked.ReadByte());
        Assert.IsFalse(Directory.Exists(directory));
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task CancelledAcceptanceDoesNotCreatePrivateBufferOrTouchSource()
    {
        using var scratch = new Scratch();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool directoryCreated = false;

        using var session = scratch.CreateSession();
        var publication = await RecoveryPrivateCopyPublisher.PublishAsync(
            session, scratch.Digest, ".docx");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            RecoveryOwnedConsumerOutput.CreateAsync(
                publication, cts.Token,
                onPrivateDirectoryCreated: _ => directoryCreated = true));

        Assert.IsFalse(directoryCreated);
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task WrongPublicationDigestFailsClosedBeforeOwnerIsReturned()
    {
        using var scratch = new Scratch();
        // Build an isolated synthetic publication with a deliberately wrong
        // receipt. Do not reopen a file still held by a publication with
        // FileShare.Delete: Windows can deny that second open.
        string temp = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bi-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string fakePath = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".docx");
        File.Copy(scratch.Source, fakePath);
        var wrong = new RecoveryPrivatePublication(
            temp, fakePath, new FileStream(fakePath,
                FileMode.Open, FileAccess.ReadWrite, FileShare.Delete),
            new RecoveryStagedCopyReceipt(scratch.Bytes.Length,
                new string('0', 64), ".docx"));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                RecoveryOwnedConsumerOutput.CreateAsync(wrong));
        }
        finally
        {
            wrong.Dispose();
            // Synthetic cleanup only; production fail-closed cleanup skips a
            // file that fails the receipt SHA check.
            File.Delete(fakePath);
            Directory.Delete(temp);
        }

        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    [TestMethod]
    public async Task UnconsumedOwnerDisposeClosesBufferAndForbidsReads()
    {
        using var scratch = new Scratch();
        string? directory = null;
        var owned = await scratch.CreateOwnedAsync(
            onPrivateDirectoryCreated: path => directory = path);
        owned.Dispose();
        Assert.IsFalse(Directory.Exists(directory));

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            owned.ReadVerifiedAsync((view, _) => Task.FromResult(view.ReadByte())));
    }

    [TestMethod]
    public async Task RevocationDrainsUnawaitedPendingReadBeforeOwnerMayCloseHandle()
    {
        using var source = new BlockedReadStream(new byte[] { 7, 8 });
        using var view = new RecoveryOwnedConsumerOutput.RevocableReadOnlyStream(
            source, CancellationToken.None);
        var buffer = new byte[1];

        Task<int> outstanding = view.ReadAsync(buffer.AsMemory()).AsTask();
        await source.Entered;
        Task revoke = view.RevokeAndDrainAsync();
        Assert.IsFalse(revoke.IsCompleted,
            "Owner must await the unfinished read, even if caller forgot its Task.");
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.ReadByte());
        Assert.IsTrue(source.CanRead, "Revoking a view never closes its owner's stream.");

        source.Release();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await outstanding);
        await revoke;
        Assert.IsTrue(source.CanRead);
    }

    [TestMethod]
    public async Task ConcurrentViewReadsAreRejectedWhileOneReadIsPending()
    {
        using var source = new BlockedReadStream(new byte[] { 4 });
        using var view = new RecoveryOwnedConsumerOutput.RevocableReadOnlyStream(
            source, CancellationToken.None);
        Task<int> outstanding = view.ReadAsync(new byte[1].AsMemory()).AsTask();
        await source.Entered;

        Assert.ThrowsExactly<InvalidOperationException>(() => view.ReadByte());
        Assert.ThrowsExactly<InvalidOperationException>(() => view.Seek(0, SeekOrigin.Begin));
        source.Release();
        Assert.AreEqual(1, await outstanding);
        await view.RevokeAndDrainAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.ReadByte());
    }

    [TestMethod]
    public async Task RealDisabledNativeAdapterCannotCreateOwnedOutput()
    {
        using var scratch = new Scratch();
        RecoveryRelativeKey key = RecoveryRelativeKey.Create(
            new string('E', 64), Guid.NewGuid().ToString("N") + ".docx");
        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            RecoveryVerifiedStreamStage.Open(new WindowsRecoveryNativeIdentityAdapter(), key));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            RecoveryVerifiedStreamStage.Open(new LinuxRecoveryNativeIdentityAdapter(), key));

        await Task.CompletedTask;
        CollectionAssert.AreEqual(scratch.Bytes, File.ReadAllBytes(scratch.Source));
    }

    private sealed class BlockedReadStream : MemoryStream
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockedReadStream(byte[] bytes) : base(bytes) { }
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult(true);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult(true);
            // Simulate a native read that does not immediately observe cancel;
            // draining must wait for actual completion, not just cancel intent.
            await _release.Task;
            return await base.ReadAsync(buffer, CancellationToken.None);
        }
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bi-" + Guid.NewGuid().ToString("N"));

        public string Source => Path.Combine(_folder, "synthetic.docx");
        public byte[] Bytes => File.ReadAllBytes(Source);
        public string Digest => Convert.ToHexString(SHA256.HashData(Bytes));

        public Scratch()
        {
            Directory.CreateDirectory(_folder);
            using var file = File.Create(Source);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            using (var xml = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open()))
                xml.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var doc = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
                doc.Write("<root/>");
        }

        public RecoveryVerifiedStreamStage CreateSession()
        {
            var adapter = new FakeAdapter(Source);
            var key = RecoveryRelativeKey.Create(
                new string('E', 64), Guid.NewGuid().ToString("N") + ".docx");
            return RecoveryVerifiedStreamStage.Open(adapter, key);
        }

        public async Task<RecoveryOwnedConsumerOutput> CreateOwnedAsync(
            Action<string>? onPrivateDirectoryCreated = null)
        {
            using var session = CreateSession();
            var publication = await RecoveryPrivateCopyPublisher.PublishAsync(
                session, Digest, ".docx");
            return await RecoveryOwnedConsumerOutput.CreateAsync(publication,
                onPrivateDirectoryCreated: onPrivateDirectoryCreated);
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeAdapter : IRecoveryNativeIdentityAdapter
    {
        private readonly string _source;
        public FakeAdapter(string source) => _source = source;
        public RecoveryNativePlatform Platform => RecoveryNativePlatform.Windows;
        public bool SupportsVerifiedDirectoryRelativeOpen => true;

        public RecoveryNativeReadLease OpenReadOnlyRelative(RecoveryRelativeKey key)
        {
            var parent = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 11, 22, 33);
            var file = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 11, 44, 55);
            return new RecoveryNativeReadLease(
                new FileStream(_source, FileMode.Open, FileAccess.Read, FileShare.Read),
                RecoveryNativePlatform.Windows, parent, parent, file, file,
                directoryRelative: true, noFollow: true);
        }
    }
}
