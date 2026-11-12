using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ce block 3: actual bounded journal + LAB RECEIPT manifest reads
/// from caller-held streams, with double readback and no trust promotion.
/// This is NOT OfficeRecoveryStore.RecoveryManifest.
/// </summary>
[TestClass]
public sealed class RecoveryHeldJournalManifestReadbackTests
{
    private static readonly string CopySha = new('B', 64);
    private static RecoveryNativeFileId File(ulong value) =>
        new(RecoveryNativePlatform.Linux, 77, value, 0);
    private static RecoveryPairIdentityEvidence Plausible() =>
        new(File(1), File(2), File(3), File(2), File(3),
            ParentHandleHeld: true, NamesOpenedRelativeToParent: true,
            NoFollowEnforced: true, WriterIsolationConfirmed: true, LockHeld: true);

    private static (Guid id, byte[] journal, byte[] manifest) Fixture()
    {
        Guid tx = Guid.NewGuid();
        string previous = RecoveryCrashJournalReconciler.GenesisSha256;
        var sequence = new[]
        {
            RecoveryNativeMutationEvent.ObserveParentAnchor,
            RecoveryNativeMutationEvent.ObserveExclusiveLock,
            RecoveryNativeMutationEvent.ObservePairNameAndIdentity
        };
        var frames = new List<RecoveryCrashJournalFrame>();
        foreach (var action in sequence)
        {
            var frame = RecoveryCrashJournalReconciler.CreateSyntheticFrame(
                tx, RecoveryNativeMutationKind.PublishPair, frames.Count + 1,
                action, previous);
            frames.Add(frame);
            previous = frame.FrameSha256;
        }
        byte[] journal = RecoveryCrashJournalCodec.EncodeSynthetic(frames);
        byte[] manifest = RecoveryHeldLabManifestCodec.EncodeCandidate(
            new RecoveryHeldLabManifestClaim(1, tx, previous, CopySha));
        return (tx, journal, manifest);
    }

    private static Task<RecoveryHeldPairReadbackResult> Inspect(
        Stream journal, Stream manifest, Guid tx, string copySha = "",
        CancellationToken ct = default) =>
        RecoveryHeldJournalManifestReadback.ReadOnHeldStreamsAsync(
            journal, manifest, tx, RecoveryNativeMutationKind.PublishPair,
            Plausible(), copySha.Length == 0 ? CopySha : copySha, ct);

    [TestMethod]
    public async Task MatchingHeldBytesRemainUntrustedAndCannotAuthorizeMutations()
    {
        var (id, journal, manifest) = Fixture();
        using var j = new MemoryStream(journal);
        using var m = new MemoryStream(manifest);
        var result = await Inspect(j, m, id);
        Assert.AreEqual(RecoveryHeldPairFinding.MatchedTwiceUntrusted, result.Finding);
        Assert.AreEqual(3, result.ClaimedJournalEvents);
        Assert.IsFalse(result.CanRestore);
        Assert.IsFalse(result.CanDelete);
        Assert.IsFalse(result.CanPublish);
        Assert.IsFalse(result.HasTrustedManifest);
        Assert.IsFalse(result.HasIndependentJournalAuthentication);
        Assert.IsFalse(result.HasVerifiedCopyContents);
    }

    [TestMethod]
    public void CodecRejectsExtraFieldsTrailingWhitespaceAndUnsupportedVersion()
    {
        var (_, _, manifest) = Fixture();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldLabManifestCodec.DecodeCandidate(
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest) + " ")));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldLabManifestCodec.DecodeCandidate(
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest)
                    .Replace("}", ",\"Extra\":1}"))));
        var claim = RecoveryHeldLabManifestCodec.DecodeCandidate(manifest);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldLabManifestCodec.EncodeCandidate(claim with { Version = 2 }));
    }

    [TestMethod]
    public void CodecRejectsMalformedHashEmptyTransactionAndInvalidGuid()
    {
        var (_, _, manifest) = Fixture();
        var valid = RecoveryHeldLabManifestCodec.DecodeCandidate(manifest);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldLabManifestCodec.EncodeCandidate(valid with {
                CopySha256 = "BAD" }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldLabManifestCodec.EncodeCandidate(valid with {
                TransactionId = Guid.Empty }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldLabManifestCodec.DecodeCandidate("{}"u8.ToArray()));
    }

    [TestMethod]
    public async Task WrongExpectedTransactionRejected()
    {
        var (_, journal, manifest) = Fixture();
        using var j = new MemoryStream(journal);
        using var m = new MemoryStream(manifest);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            Inspect(j, m, Guid.NewGuid()));
    }

    [TestMethod]
    public async Task WrongCopyDigestRejected()
    {
        var (id, journal, manifest) = Fixture();
        using var j = new MemoryStream(journal);
        using var m = new MemoryStream(manifest);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            Inspect(j, m, id, new string('A', 64)));
    }

    [TestMethod]
    public async Task ManifestHeadCannotDifferFromActualJournal()
    {
        var (id, journal, manifest) = Fixture();
        var old = RecoveryHeldLabManifestCodec.DecodeCandidate(manifest);
        var altered = RecoveryHeldLabManifestCodec.EncodeCandidate(
            old with { JournalHeadSha256 = new string('A', 64) });
        using var j = new MemoryStream(journal);
        using var m = new MemoryStream(altered);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            Inspect(j, m, id));
    }

    [TestMethod]
    public async Task TruncatedJournalNeverMatchesLabReceipt()
    {
        var (id, journal, manifest) = Fixture();
        using var j = new MemoryStream(journal[..^1]);
        using var m = new MemoryStream(manifest);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            Inspect(j, m, id));
    }

    [TestMethod]
    public async Task ManifestOverBoundIsRejectedBeforeAllocation()
    {
        var (id, journal, _) = Fixture();
        using var j = new MemoryStream(journal);
        using var m = new MemoryStream(new byte[
            RecoveryHeldLabManifestCodec.MaxManifestBytes + 1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            Inspect(j, m, id));
    }

    [TestMethod]
    public async Task SameStreamUsedForBothRolesIsRejected()
    {
        var (id, journal, _) = Fixture();
        using var j = new MemoryStream(journal);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            Inspect(j, j, id));
    }

    [TestMethod]
    public async Task PreCancelledReadIsRejectedWithoutMutation()
    {
        var (id, journal, manifest) = Fixture();
        using var j = new MemoryStream(journal);
        using var m = new MemoryStream(manifest);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            Inspect(j, m, id, ct: cts.Token));
        CollectionAssert.AreEqual(journal, j.ToArray());
        CollectionAssert.AreEqual(manifest, m.ToArray());
    }

    [TestMethod]
    public async Task ModifiedManifestBetweenReadbacksFailsClosed()
    {
        var (id, journal, manifest) = Fixture();
        using var j = new MemoryStream(journal);
        using var m = new MutatingSecondReadStream(manifest);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            Inspect(j, m, id));
    }

    [TestMethod]
    public async Task InvalidShortManifestRejectedWithoutFileDeletion()
    {
        var (id, journal, _) = Fixture();
        using var j = new MemoryStream(journal);
        using var m = new MemoryStream(new byte[] { 1, 2, 3 });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            Inspect(j, m, id));
        Assert.IsFalse(RecoveryHeldJournalManifestReadback.CanAuthorizeCleanup);
    }

    [TestMethod]
    public async Task ExplicitLabFileHandlesCanBeReadAndReopened()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) ||
            Environment.GetEnvironmentVariable("GD31J27CE_HELD_READ_LAB") != "1")
            return;
        var (id, journal, manifest) = Fixture();
        string parent = Path.Combine(Path.GetTempPath(),
            "getdoc-ce-held-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            string journalPath = Path.Combine(parent, "journal.test");
            string manifestPath = Path.Combine(parent, "manifest.test");
            // Test-owned files only. The diagnostic reader itself never
            // creates, writes, renames or removes a file.
            File.WriteAllBytes(journalPath, journal);
            File.WriteAllBytes(manifestPath, manifest);
            await using var j = new FileStream(journalPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            await using var m = new FileStream(manifestPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var result = await Inspect(j, m, id);
            Assert.IsTrue(result.JournalReadFromNativeFileStream);
            Assert.IsTrue(result.ManifestReadFromNativeFileStream);
            Assert.IsFalse(result.CanRestore);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private sealed class MutatingSecondReadStream(byte[] original) : MemoryStream(original)
    {
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (++_reads == 2)
            {
                long originalOffset = Position;
                Position = 0;
                // Disrupt canonical representation on second pass.
                WriteByte((byte)'?');
                Position = originalOffset;
            }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
