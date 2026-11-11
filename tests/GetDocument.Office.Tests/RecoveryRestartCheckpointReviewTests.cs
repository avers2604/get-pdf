using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class RecoveryRestartCheckpointReviewTests
{
    private static readonly string PriorMac = new('0', 64);
    private static byte[] Key() => Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    private static RecoveryNativeFileId File(ulong inode) =>
        new(RecoveryNativePlatform.Linux, 88, inode, 0);

    private static RecoveryPairIdentityEvidence Pair() =>
        new(File(1), File(2), File(3), File(2), File(3),
            ParentHandleHeld: true, NamesOpenedRelativeToParent: true,
            NoFollowEnforced: true, WriterIsolationConfirmed: true, LockHeld: true);

    private static RecoveryRestartFileIdSnapshot Stable() =>
        new(File(1), File(1), File(2), File(2), File(3), File(3));

    private static RecoveryNativeMutationEvent[] Publish() =>
        new[]
        {
            RecoveryNativeMutationEvent.ObserveParentAnchor,
            RecoveryNativeMutationEvent.ObserveExclusiveLock,
            RecoveryNativeMutationEvent.ObservePairNameAndIdentity,
            RecoveryNativeMutationEvent.FlushCopyContent,
            RecoveryNativeMutationEvent.RevalidateCopyName,
            RecoveryNativeMutationEvent.PublishCopyNoReplace,
            RecoveryNativeMutationEvent.SyncParentDirectory,
            RecoveryNativeMutationEvent.FlushManifestContent,
            RecoveryNativeMutationEvent.RevalidateManifestName,
            RecoveryNativeMutationEvent.PublishManifestNoReplace,
            RecoveryNativeMutationEvent.SyncParentDirectory
        };

    private static RecoveryNativeMutationEvent[] Delete() =>
        new[]
        {
            RecoveryNativeMutationEvent.ObserveParentAnchor,
            RecoveryNativeMutationEvent.ObserveExclusiveLock,
            RecoveryNativeMutationEvent.ObservePairNameAndIdentity,
            RecoveryNativeMutationEvent.RevalidateCopyName,
            RecoveryNativeMutationEvent.UnlinkCopyEntry,
            RecoveryNativeMutationEvent.SyncParentDirectory,
            RecoveryNativeMutationEvent.RevalidateManifestName,
            RecoveryNativeMutationEvent.UnlinkManifestEntry,
            RecoveryNativeMutationEvent.SyncParentDirectory
        };

    private static (byte[] bytes, RecoveryProtectedAnchorCandidate anchor) Build(
        Guid tx, RecoveryNativeMutationKind kind,
        params RecoveryNativeMutationEvent[] actions)
    {
        string prev = RecoveryCrashJournalReconciler.GenesisSha256;
        var frames = new List<RecoveryCrashJournalFrame>();
        foreach (var action in actions)
        {
            var frame = RecoveryCrashJournalReconciler.CreateSyntheticFrame(
                tx, kind, frames.Count + 1, action, prev);
            prev = frame.FrameSha256;
            frames.Add(frame);
        }
        var bytes = RecoveryCrashJournalCodec.EncodeSynthetic(frames);
        var anchor = RecoveryProtectedAnchorProtocol.CreateSynthetic(
            tx, 9, prev, PriorMac, Key());
        return (bytes, anchor);
    }

    private static RecoveryRestartReviewResult Inspect(
        Guid tx, RecoveryNativeMutationKind kind,
        byte[] bytes, RecoveryProtectedAnchorCandidate anchor,
        RecoveryCrashArtifactSnapshot artifacts,
        RecoveryRestartFileIdSnapshot fileIds,
        byte[]? key = null, CancellationToken cancellationToken = default) =>
        RecoveryRestartCheckpointReview.Inspect(
            bytes, anchor, key ?? Key(), tx, kind, 9, PriorMac,
            Pair(), artifacts, fileIds, cancellationToken);

    [TestMethod]
    public void CompletePublishStillNeedsManualNativeAttestation()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair, Publish());
        var result = Inspect(tx, RecoveryNativeMutationKind.PublishPair,
            bytes, anchor, new(RecoveryCrashArtifactClaim.RegularFileObserved,
                RecoveryCrashArtifactClaim.RegularFileObserved), Stable());
        Assert.AreEqual(RecoveryRestartFinding.MatchedClaimsOnly, result.Finding);
        Assert.IsFalse(result.CanRestore);
        Assert.IsFalse(result.CanDelete);
        Assert.IsFalse(result.CanResumePublication);
    }

    [TestMethod]
    public void CompleteDeleteIsNotPermissionForCleanup()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.DeletePair, Delete());
        var result = Inspect(tx, RecoveryNativeMutationKind.DeletePair,
            bytes, anchor, new(RecoveryCrashArtifactClaim.Missing,
                RecoveryCrashArtifactClaim.Missing), default);
        Assert.AreEqual(RecoveryRestartFinding.MatchedClaimsOnly, result.Finding);
        Assert.IsFalse(result.CanDelete);
    }

    [TestMethod]
    public void WrongKeyBlocksBeforeTrustingJournal()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair, Publish());
        byte[] key = Key();
        key[0] = 123;
        Assert.AreEqual(RecoveryRestartFinding.AnchorRejected,
            Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                bytes, anchor, new(RecoveryCrashArtifactClaim.Missing,
                    RecoveryCrashArtifactClaim.Missing), Stable(), key).Finding);
    }

    [TestMethod]
    public void ReplayedGenerationIsRejected()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair, Publish());
        var old = RecoveryProtectedAnchorProtocol.CreateSynthetic(tx, 8,
            anchor.JournalHeadSha256, PriorMac, Key());
        Assert.AreEqual(RecoveryRestartFinding.AnchorRejected,
            Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                bytes, old, new(RecoveryCrashArtifactClaim.Missing,
                    RecoveryCrashArtifactClaim.Missing), Stable()).Finding);
    }

    [TestMethod]
    public void TruncatedJournalCannotMatchProtectedHead()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair, Publish());
        var truncated = Build(tx, RecoveryNativeMutationKind.PublishPair,
            Publish().Take(3).ToArray()).bytes;
        Assert.AreEqual(RecoveryRestartFinding.InvalidJournal,
            Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                truncated, anchor, new(RecoveryCrashArtifactClaim.Missing,
                    RecoveryCrashArtifactClaim.Missing), Stable()).Finding);
    }

    [TestMethod]
    public void InvalidJsonIsNeverTreatedAsNoJournal()
    {
        Guid tx = Guid.NewGuid();
        var (_, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair, Publish());
        Assert.AreEqual(RecoveryRestartFinding.InvalidJournal,
            Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                new byte[] { 0x7B, 0x21 }, anchor,
                new(RecoveryCrashArtifactClaim.Missing,
                    RecoveryCrashArtifactClaim.Missing), Stable()).Finding);
    }

    [TestMethod]
    public void OrphanCopyNeedsHumanReviewWithoutCleanup()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair,
            Publish().Take(6).ToArray());
        var result = Inspect(tx, RecoveryNativeMutationKind.PublishPair,
            bytes, anchor, new(RecoveryCrashArtifactClaim.RegularFileObserved,
                RecoveryCrashArtifactClaim.Missing), Stable());
        Assert.AreEqual(RecoveryRestartFinding.OrphanRequiresReview, result.Finding);
        Assert.IsFalse(result.CanDelete);
    }

    [TestMethod]
    public void UntrustedSymlinkAndUnknownMetadataRequireNativeReview()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair,
            Publish().Take(3).ToArray());
        foreach (var status in new[]
        {
            RecoveryCrashArtifactClaim.UntrustedLinkOrNonFile,
            RecoveryCrashArtifactClaim.MetadataUnavailable,
            RecoveryCrashArtifactClaim.NotObserved
        })
        {
            var result = Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                bytes, anchor, new(status, RecoveryCrashArtifactClaim.Missing), Stable());
            Assert.AreEqual(RecoveryRestartFinding.NeedsNativeReadback, result.Finding);
        }
    }

    [TestMethod]
    public void ReopenedCopyIdChangedIsReportedNotRestored()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair, Publish());
        var ids = Stable() with { CopyAtRecheck = File(99) };
        Assert.AreEqual(RecoveryRestartFinding.ReopenedIdentityMismatch,
            Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                bytes, anchor, new(RecoveryCrashArtifactClaim.RegularFileObserved,
                    RecoveryCrashArtifactClaim.RegularFileObserved), ids).Finding);
    }

    [TestMethod]
    public void AliasedManifestAndCopyAreRefused()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair, Publish());
        var ids = Stable() with { ManifestAtOpen = File(2), ManifestAtRecheck = File(2) };
        Assert.AreEqual(RecoveryRestartFinding.ReopenedIdentityMismatch,
            Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                bytes, anchor, new(RecoveryCrashArtifactClaim.RegularFileObserved,
                    RecoveryCrashArtifactClaim.RegularFileObserved), ids).Finding);
    }

    [TestMethod]
    public void CompletedPublishWithMissingManifestIsContradiction()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair, Publish());
        Assert.AreEqual(RecoveryRestartFinding.JournalArtifactConflict,
            Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                bytes, anchor, new(RecoveryCrashArtifactClaim.RegularFileObserved,
                    RecoveryCrashArtifactClaim.Missing), Stable()).Finding);
    }

    [TestMethod]
    public void InterruptedBeforeChangeRemainsIncomplete()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair,
            Publish().Take(3).ToArray());
        Assert.AreEqual(RecoveryRestartFinding.IncompleteOperation,
            Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                bytes, anchor, new(RecoveryCrashArtifactClaim.Missing,
                    RecoveryCrashArtifactClaim.Missing), Stable()).Finding);
    }

    [TestMethod]
    public void EmptyJournalWithValidGenesisStillDoesNotProveCleanStart()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.DeletePair);
        Assert.AreEqual(RecoveryRestartFinding.NoJournalEvidence,
            Inspect(tx, RecoveryNativeMutationKind.DeletePair,
                bytes, anchor, new(RecoveryCrashArtifactClaim.Missing,
                    RecoveryCrashArtifactClaim.Missing), default).Finding);
    }

    [TestMethod]
    public void CancellationBeforeInspectNeverGrantsAuthority()
    {
        Guid tx = Guid.NewGuid();
        var (bytes, anchor) = Build(tx, RecoveryNativeMutationKind.PublishPair, Publish());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            Inspect(tx, RecoveryNativeMutationKind.PublishPair,
                bytes, anchor, new(RecoveryCrashArtifactClaim.RegularFileObserved,
                    RecoveryCrashArtifactClaim.RegularFileObserved), Stable(),
                cancellationToken: cts.Token));
    }
}
