using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bf. Synthetic, isolated staging tests. These fake a *previously
/// verified* descriptor but DO NOT claim genuine OS no-follow semantics.
/// The actual Windows/Linux adapters remain disabled.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryVerifiedStagingTests
{
    [TestMethod]
    public async Task ValidCopyIsStagedFromVerifiedLeaseAndSourceHandleIsReleased()
    {
        using var fixture = new Scratch(validZip: true);
        RecoveryNativeReadLease? sourceLease = null;
        var adapter = NewFake(() => sourceLease = NewLease(fixture.FilePath));
        using var session = RecoveryVerifiedStreamStage.Open(adapter, NewKey());
        using var staging = new MemoryStream();

        RecoveryStagedCopyReceipt receipt = await session.StageToAsync(
            staging, fixture.Sha256, ".docx");

        Assert.AreEqual(fixture.Bytes.Length, receipt.Length);
        Assert.AreEqual(fixture.Sha256, receipt.Sha256);
        Assert.AreEqual(".docx", receipt.Extension);
        CollectionAssert.AreEqual(fixture.Bytes, staging.ToArray());
        Assert.IsTrue(staging.CanRead, "The staging stream is caller-owned.");
        Assert.AreEqual(0L, staging.Position);
        Assert.IsNotNull(sourceLease);
        Assert.IsFalse(sourceLease.Stream.CanRead, "The source lease is released.");
        CollectionAssert.AreEqual(fixture.Bytes, File.ReadAllBytes(fixture.FilePath));
    }

    [TestMethod]
    public async Task WrongDigestCleansPrivateStagingAndClosesSourceHandle()
    {
        using var fixture = new Scratch(validZip: true);
        RecoveryNativeReadLease? sourceLease = null;
        using var session = RecoveryVerifiedStreamStage.Open(
            NewFake(() => sourceLease = NewLease(fixture.FilePath)), NewKey());
        using var staging = new MemoryStream();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await session.StageToAsync(staging, new string('0', 64), ".docx"));

        Assert.AreEqual(0L, staging.Length);
        Assert.IsTrue(staging.CanWrite);
        Assert.IsNotNull(sourceLease);
        Assert.IsFalse(sourceLease.Stream.CanRead);
        CollectionAssert.AreEqual(fixture.Bytes, File.ReadAllBytes(fixture.FilePath));
    }

    [TestMethod]
    public async Task CorrectDigestOfInvalidZipIsNotARecoveryReceipt()
    {
        using var fixture = new Scratch(validZip: false);
        using var session = RecoveryVerifiedStreamStage.Open(
            NewFake(() => NewLease(fixture.FilePath)), NewKey());
        using var staging = new MemoryStream();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await session.StageToAsync(staging, fixture.Sha256, ".docx"));

        Assert.AreEqual(0L, staging.Length);
        Assert.IsTrue(File.Exists(fixture.FilePath));
    }

    [TestMethod]
    public async Task MidWriteCancellationReleasesLeaseAndInvalidatesStaging()
    {
        using var fixture = new Scratch(validZip: true);
        using var cts = new CancellationTokenSource();
        RecoveryNativeReadLease? sourceLease = null;
        using var session = RecoveryVerifiedStreamStage.Open(
            NewFake(() => sourceLease = NewLease(fixture.FilePath)), NewKey());
        using var staging = new CancelAfterFirstWriteStream(cts);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await session.StageToAsync(staging, fixture.Sha256, ".docx", cts.Token));

        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.AreEqual(0L, staging.Length);
        Assert.IsNotNull(sourceLease);
        Assert.IsFalse(sourceLease.Stream.CanRead);
        CollectionAssert.AreEqual(fixture.Bytes, File.ReadAllBytes(fixture.FilePath));
    }

    [TestMethod]
    public async Task PreexistingStagingBytesAreNeverErasedOrOverwritten()
    {
        using var fixture = new Scratch(validZip: true);
        using var session = RecoveryVerifiedStreamStage.Open(
            NewFake(() => NewLease(fixture.FilePath)), NewKey());
        byte[] original = Encoding.UTF8.GetBytes("PRESERVE_EXISTING_STAGE");
        using var staging = new MemoryStream();
        staging.Write(original);
        staging.Position = 0;

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await session.StageToAsync(staging, fixture.Sha256, ".docx"));

        CollectionAssert.AreEqual(original, staging.ToArray());
        Assert.IsTrue(staging.CanRead);
        Assert.IsTrue(File.Exists(fixture.FilePath));
    }

    [TestMethod]
    public async Task AVerifiedSessionIsSingleUseEvenAfterSuccessfulTransfer()
    {
        using var fixture = new Scratch(validZip: true);
        using var session = RecoveryVerifiedStreamStage.Open(
            NewFake(() => NewLease(fixture.FilePath)), NewKey());
        using var staging = new MemoryStream();
        await session.StageToAsync(staging, fixture.Sha256, ".docx");

        using var second = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await session.StageToAsync(second, fixture.Sha256, ".docx"));
        Assert.AreEqual(0L, second.Length);
    }

    [TestMethod]
    public async Task DisposingUnconsumedSessionClosesSourceWithoutTouchingStage()
    {
        using var fixture = new Scratch(validZip: true);
        RecoveryNativeReadLease? sourceLease = null;
        var session = RecoveryVerifiedStreamStage.Open(
            NewFake(() => sourceLease = NewLease(fixture.FilePath)), NewKey());
        await session.DisposeAsync();

        Assert.IsNotNull(sourceLease);
        Assert.IsFalse(sourceLease.Stream.CanRead);
        using var staging = new MemoryStream();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await session.StageToAsync(staging, fixture.Sha256, ".docx"));
        Assert.AreEqual(0L, staging.Length);
    }

    [TestMethod]
    public void DisabledRealNativeAdapterCannotCreateAStagingSession()
    {
        RecoveryRelativeKey key = NewKey();
        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            RecoveryVerifiedStreamStage.Open(new WindowsRecoveryNativeIdentityAdapter(), key));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            RecoveryVerifiedStreamStage.Open(new LinuxRecoveryNativeIdentityAdapter(), key));
    }

    private static RecoveryRelativeKey NewKey() => RecoveryRelativeKey.Create(
        new string('D', 64), Guid.NewGuid().ToString("N") + ".docx");

    private static IRecoveryNativeIdentityAdapter NewFake(
        Func<RecoveryNativeReadLease> open) => new FakeAdapter(open);

    private static RecoveryNativeReadLease NewLease(string path)
    {
        var parent = new RecoveryNativeFileId(RecoveryNativePlatform.Windows, 100, 200, 300);
        var copy = new RecoveryNativeFileId(RecoveryNativePlatform.Windows, 100, 400, 500);
        return new RecoveryNativeReadLease(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            RecoveryNativePlatform.Windows, parent, parent, copy, copy,
            directoryRelative: true, noFollow: true);
    }

    private sealed class FakeAdapter : IRecoveryNativeIdentityAdapter
    {
        private readonly Func<RecoveryNativeReadLease> _open;
        public RecoveryNativePlatform Platform => RecoveryNativePlatform.Windows;
        public bool SupportsVerifiedDirectoryRelativeOpen => true;
        public FakeAdapter(Func<RecoveryNativeReadLease> open) => _open = open;
        public RecoveryNativeReadLease OpenReadOnlyRelative(RecoveryRelativeKey key) => _open();
    }

    private sealed class CancelAfterFirstWriteStream : MemoryStream
    {
        private readonly CancellationTokenSource _cts;
        public CancelAfterFirstWriteStream(CancellationTokenSource cts) => _cts = cts;
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            _cts.Cancel();
        }
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bf-" + Guid.NewGuid().ToString("N"));

        public string FilePath => Path.Combine(_dir, "synthetic.docx");
        public byte[] Bytes => File.ReadAllBytes(FilePath);
        public string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes));

        public Scratch(bool validZip)
        {
            Directory.CreateDirectory(_dir);
            if (!validZip)
            {
                File.WriteAllText(FilePath, "NOT_A_VALID_OOXML_CONTAINER");
                return;
            }
            using var output = File.Create(FilePath);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            using (var contentTypes = new StreamWriter(
                zip.CreateEntry("[Content_Types].xml").Open()))
                contentTypes.Write(
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var doc = new StreamWriter(
                zip.CreateEntry("word/document.xml").Open()))
                doc.Write("<root/>");
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
    }
}
