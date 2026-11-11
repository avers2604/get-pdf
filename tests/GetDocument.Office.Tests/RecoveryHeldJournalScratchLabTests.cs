using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ca block 1–2: lab-only held-stream content I/O.
/// All streams are caller-owned. Never production recovery authorization.
/// </summary>
[TestClass]
public sealed class RecoveryHeldJournalScratchLabTests
{
    private static RecoveryNativeFileId File(ulong value) =>
        new(RecoveryNativePlatform.Linux, 17, value, 0);

    private static RecoveryPairIdentityEvidence Plausible() =>
        new(File(1), File(2), File(3), File(2), File(3),
            ParentHandleHeld: true, NamesOpenedRelativeToParent: true,
            NoFollowEnforced: true, WriterIsolationConfirmed: true, LockHeld: true);

    private static (Guid id, byte[] bytes) Candidate()
    {
        Guid id = Guid.NewGuid();
        var frame1 = RecoveryCrashJournalReconciler.CreateSyntheticFrame(id,
            RecoveryNativeMutationKind.PublishPair, 1,
            RecoveryNativeMutationEvent.ObserveParentAnchor,
            RecoveryCrashJournalReconciler.GenesisSha256);
        var frame2 = RecoveryCrashJournalReconciler.CreateSyntheticFrame(id,
            RecoveryNativeMutationKind.PublishPair, 2,
            RecoveryNativeMutationEvent.ObserveExclusiveLock, frame1.FrameSha256);
        var frame3 = RecoveryCrashJournalReconciler.CreateSyntheticFrame(id,
            RecoveryNativeMutationKind.PublishPair, 3,
            RecoveryNativeMutationEvent.ObservePairNameAndIdentity, frame2.FrameSha256);
        return (id, RecoveryCrashJournalCodec.EncodeSynthetic(
            new[] { frame1, frame2, frame3 }));
    }

    private static Task<RecoveryHeldJournalScratchReceipt> Stage(
        Stream stream, byte[] bytes, Guid id, CancellationToken token = default) =>
        RecoveryHeldJournalScratchLab.StageOnHeldStreamAsync(stream, bytes, id,
            RecoveryNativeMutationKind.PublishPair, Plausible(), token);

    private static Task<RecoveryHeldJournalReadbackReport> Read(
        Stream stream, Guid id, CancellationToken token = default) =>
        RecoveryHeldJournalScratchLab.ReadOnHeldStreamAsync(stream, id,
            RecoveryNativeMutationKind.PublishPair, Plausible(), token);

    [TestMethod]
    public async Task ValidCanonicalJournalStagesAndReadsBackOnSameHeldStream()
    {
        var (id, bytes) = Candidate();
        using var scratch = new MemoryStream();
        var receipt = await Stage(scratch, bytes, id);
        Assert.AreEqual(bytes.Length, receipt.ByteCount);
        Assert.IsFalse(receipt.CanAuthorizeRecovery);
        Assert.IsFalse(receipt.DirectoryEntryDurabilityVerified);
        Assert.IsFalse(receipt.ContentDurabilityVerified);
        Assert.IsTrue(scratch.CanRead);
        Assert.AreEqual((long)bytes.Length, scratch.Length);
        var report = await Read(scratch, id);
        Assert.AreEqual(3, report.ClaimedEvents);
        Assert.IsFalse(report.CanRestore);
        Assert.IsFalse(report.CanDelete);
        Assert.AreEqual(receipt.ContentSha256, report.ContentSha256);
    }

    [TestMethod]
    public async Task RealOwnedFixtureFileCanBeReopenedButRemainsUntrusted()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        var (id, bytes) = Candidate();
        string path = Path.Combine(Path.GetTempPath(),
            "getdoc-ca-scratch-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await using (var created = new FileStream(path, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
                bufferSize: 4096, options: FileOptions.Asynchronous))
            {
                var receipt = await Stage(created, bytes, id);
                Assert.IsTrue(receipt.FileContentFlushAttempted);
                Assert.IsFalse(receipt.DirectoryEntryDurabilityVerified);
            }
            await using (var reopened = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 4096, options: FileOptions.Asynchronous))
            {
                var report = await Read(reopened, id);
                Assert.IsTrue(report.WasReadFromHeldFileStream);
                Assert.IsFalse(report.CanRestore);
            }
        }
        finally
        {
            // Test-only cleanup of a unique test-owned fixture; NEVER
            // invoked by RecoveryHeldJournalScratchLab itself.
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ExistingBytesAreNotOverwritten()
    {
        var (id, bytes) = Candidate();
        using var scratch = new MemoryStream();
        scratch.WriteByte(0x2A);
        scratch.Position = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Stage(scratch, bytes, id));
        CollectionAssert.AreEqual(new byte[] { 0x2A }, scratch.ToArray());
    }

    [TestMethod]
    public async Task NonSeekableOrNonWritableSourceIsRejected()
    {
        var (id, bytes) = Candidate();
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => Stage(Stream.Null, bytes, id));
        using var readOnly = new MemoryStream(bytes, writable: false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => Stage(readOnly, bytes, id));
    }

    [TestMethod]
    public async Task NonCanonicalJsonDoesNotWriteAnything()
    {
        var (id, bytes) = Candidate();
        using var scratch = new MemoryStream();
        var altered = System.Text.Encoding.UTF8.GetBytes(
            " " + System.Text.Encoding.UTF8.GetString(bytes));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Stage(scratch, altered, id));
        Assert.AreEqual(0, scratch.Length);
    }

    [TestMethod]
    public async Task EmptyJournalIsNotAcceptedForScratchWrite()
    {
        Guid id = Guid.NewGuid();
        using var scratch = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Stage(scratch, "[]"u8.ToArray(), id));
        Assert.AreEqual(0, scratch.Length);
    }

    [TestMethod]
    public async Task DifferentTransactionCannotBeStaged()
    {
        var (_, bytes) = Candidate();
        using var scratch = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Stage(scratch, bytes, Guid.NewGuid()));
        Assert.AreEqual(0, scratch.Length);
    }

    [TestMethod]
    public async Task CancellationBeforeWriteDoesNotMutateScratch()
    {
        var (id, bytes) = Candidate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var scratch = new MemoryStream();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => Stage(scratch, bytes, id, cts.Token));
        Assert.AreEqual(0, scratch.Length);
    }

    [TestMethod]
    public async Task CancellationAfterWriteLeavesDataForManualReview()
    {
        var (id, bytes) = Candidate();
        using var cts = new CancellationTokenSource();
        using var scratch = new CancelAfterWriteStream(cts);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => Stage(scratch, bytes, id, cts.Token));
        Assert.AreEqual((long)bytes.Length, scratch.Length);
        Assert.IsFalse(RecoveryHeldJournalScratchLab.ProductionWriterEnabled);
    }

    [TestMethod]
    public async Task InjectedPartialWriteFaultIsNotRolledBackOrDeleted()
    {
        var (id, bytes) = Candidate();
        using var scratch = new PartialFailureStream();
        await Assert.ThrowsExactlyAsync<IOException>(
            () => Stage(scratch, bytes, id));
        Assert.IsTrue(scratch.Length > 0);
        Assert.IsTrue(scratch.Length < bytes.Length);
        Assert.IsFalse(RecoveryHeldJournalScratchLab.CanCleanupByPath);
    }

    [TestMethod]
    public async Task TruncatedExistingHeldStreamIsInvalidJournal()
    {
        var (id, bytes) = Candidate();
        using var scratch = new MemoryStream(bytes[..^1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Read(scratch, id));
    }

    [TestMethod]
    public async Task ExtraByteInExistingHeldStreamIsInvalidJournal()
    {
        var (id, bytes) = Candidate();
        using var scratch = new MemoryStream(bytes.Concat(new byte[] { 0 }).ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Read(scratch, id));
    }

    [TestMethod]
    public async Task OversizedHeldStreamIsRejectedBeforeAllocation()
    {
        var (id, _) = Candidate();
        using var scratch = new MemoryStream(
            new byte[RecoveryCrashJournalCodec.MaxEnvelopeBytes + 1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Read(scratch, id));
    }

    [TestMethod]
    public async Task CallerBufferMutationAtWriteCannotChangeValidatedSnapshot()
    {
        var (id, encoded) = Candidate();
        byte[] original = (byte[])encoded.Clone();
        using var scratch = new MutateCallerBeforeWriteStream(
            () => Array.Fill(encoded, (byte)0x21));
        var receipt = await Stage(scratch, encoded, id);
        Assert.AreEqual((long)original.Length, scratch.Length);
        CollectionAssert.AreEqual(original, scratch.ToArray());
        Assert.AreEqual(3, (await Read(scratch, id)).ClaimedEvents);
        Assert.IsFalse(receipt.CanAuthorizeRecovery);
    }

    [TestMethod]
    public async Task InvalidOrderInValidCanonicalEnvelopeFailsBeforeWrite()
    {
        var (id, bytes) = Candidate();
        var original = RecoveryCrashJournalCodec.DecodeCandidate(bytes);
        var first = original[0] with
        {
            Event = RecoveryNativeMutationEvent.UnlinkCopyEntry
        };
        byte[] invalid = RecoveryCrashJournalCodec.EncodeSynthetic(new[] { first });
        using var scratch = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Stage(scratch, invalid, id));
        Assert.AreEqual(0, scratch.Length);
    }

    [TestMethod]
    public async Task ReadbackOnWrongTransactionNeverAuthorizesRestore()
    {
        var (id, bytes) = Candidate();
        using var scratch = new MemoryStream(bytes);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Read(scratch, Guid.NewGuid()));
        Assert.IsFalse(RecoveryHeldJournalScratchLab.ProductionWriterEnabled);
    }

    private sealed class MutateCallerBeforeWriteStream(Action mutate) : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            mutate();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class CancelAfterWriteStream(CancellationTokenSource cancellation)
        : MemoryStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            cancellation.Cancel();
        }
    }

    private sealed class PartialFailureStream : MemoryStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer[..Math.Max(1, buffer.Length / 2)],
                cancellationToken);
            throw new IOException("Injected power-loss-like partial write.");
        }
    }
}
