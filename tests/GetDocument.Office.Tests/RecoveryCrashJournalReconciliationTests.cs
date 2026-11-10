using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27by: synthetic reboot/partial-journal reconciliation contracts.
/// Never touches real documents and never authorizes cleanup or restore.
/// </summary>
[TestClass]
public sealed class RecoveryCrashJournalReconciliationTests
{
    private static RecoveryNativeFileId File(ulong inode) =>
        new(RecoveryNativePlatform.Linux, 77, inode, 0);

    private static RecoveryPairIdentityEvidence SyntheticPair() =>
        new(File(1), File(2), File(3), File(2), File(3),
            ParentHandleHeld: true, NamesOpenedRelativeToParent: true,
            NoFollowEnforced: true, WriterIsolationConfirmed: true,
            LockHeld: true);

    private static readonly RecoveryCrashArtifactSnapshot BothClaimed =
        new(RecoveryCrashArtifactClaim.RegularFileObserved,
            RecoveryCrashArtifactClaim.RegularFileObserved);

    private static readonly RecoveryCrashArtifactSnapshot BothMissing =
        new(RecoveryCrashArtifactClaim.Missing,
            RecoveryCrashArtifactClaim.Missing);

    private static RecoveryNativeMutationEvent[] StartEvents() =>
        new[]
        {
            RecoveryNativeMutationEvent.ObserveParentAnchor,
            RecoveryNativeMutationEvent.ObserveExclusiveLock,
            RecoveryNativeMutationEvent.ObservePairNameAndIdentity
        };

    private static RecoveryNativeMutationEvent[] PublishEvents() =>
        StartEvents().Concat(new[]
        {
            RecoveryNativeMutationEvent.FlushCopyContent,
            RecoveryNativeMutationEvent.RevalidateCopyName,
            RecoveryNativeMutationEvent.PublishCopyNoReplace,
            RecoveryNativeMutationEvent.SyncParentDirectory,
            RecoveryNativeMutationEvent.FlushManifestContent,
            RecoveryNativeMutationEvent.RevalidateManifestName,
            RecoveryNativeMutationEvent.PublishManifestNoReplace,
            RecoveryNativeMutationEvent.SyncParentDirectory
        }).ToArray();

    private static RecoveryNativeMutationEvent[] DeleteEvents() =>
        StartEvents().Concat(new[]
        {
            RecoveryNativeMutationEvent.RevalidateCopyName,
            RecoveryNativeMutationEvent.UnlinkCopyEntry,
            RecoveryNativeMutationEvent.SyncParentDirectory,
            RecoveryNativeMutationEvent.RevalidateManifestName,
            RecoveryNativeMutationEvent.UnlinkManifestEntry,
            RecoveryNativeMutationEvent.SyncParentDirectory
        }).ToArray();

    private static RecoveryCrashJournalFrame[] Frames(
        Guid transactionId, RecoveryNativeMutationKind kind,
        params RecoveryNativeMutationEvent[] events)
    {
        var frames = new List<RecoveryCrashJournalFrame>();
        string previous = RecoveryCrashJournalReconciler.GenesisSha256;
        foreach (var action in events)
        {
            var frame = RecoveryCrashJournalReconciler.CreateSyntheticFrame(
                transactionId, kind, frames.Count + 1, action, previous);
            frames.Add(frame);
            previous = frame.FrameSha256;
        }
        return frames.ToArray();
    }

    private static RecoveryCrashReconciliationReport Analyze(
        Guid id, RecoveryNativeMutationKind kind,
        IReadOnlyList<RecoveryCrashJournalFrame> frames,
        RecoveryCrashArtifactSnapshot snapshot, string? anchoredHead = null) =>
        RecoveryCrashJournalReconciler.Analyze(
            frames, id, kind, SyntheticPair(), snapshot, anchoredHead);

    [TestMethod]
    public void NoJournalIsUnknownAndNeverAReasonToDelete()
    {
        Guid id = Guid.NewGuid();
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            Array.Empty<RecoveryCrashJournalFrame>(), BothMissing);
        Assert.AreEqual(RecoveryCrashReconciliationDisposition.NoJournalEvidence,
            report.Disposition);
        Assert.AreEqual(0, report.ReplayedFrames);
        Assert.IsFalse(report.CanAuthorizeCleanup);
        Assert.IsFalse(report.CanAuthorizeRestoration);
    }

    [TestMethod]
    public void PreMutationPrefixIsReadOnlyUnfinishedAndNotCleanable()
    {
        Guid id = Guid.NewGuid();
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            Frames(id, RecoveryNativeMutationKind.PublishPair, StartEvents()),
            BothMissing);
        Assert.AreEqual(
            RecoveryCrashReconciliationDisposition.InterruptedBeforeMutationClaim,
            report.Disposition);
        Assert.IsFalse(report.CanAuthorizePublication);
    }

    [TestMethod]
    public void CopyPublishedWithoutManifestIsOrphanForReviewNotRollback()
    {
        Guid id = Guid.NewGuid();
        var events = StartEvents().Concat(new[]
        {
            RecoveryNativeMutationEvent.FlushCopyContent,
            RecoveryNativeMutationEvent.RevalidateCopyName,
            RecoveryNativeMutationEvent.PublishCopyNoReplace
        }).ToArray();
        var snapshot = new RecoveryCrashArtifactSnapshot(
            RecoveryCrashArtifactClaim.RegularFileObserved,
            RecoveryCrashArtifactClaim.Missing);
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            Frames(id, RecoveryNativeMutationKind.PublishPair, events), snapshot);
        Assert.AreEqual(RecoveryCrashReconciliationDisposition.CopyWithoutManifest,
            report.Disposition);
        Assert.IsFalse(report.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void ManifestWithoutCopyIsAlwaysSuspicious()
    {
        Guid id = Guid.NewGuid();
        var snapshot = new RecoveryCrashArtifactSnapshot(
            RecoveryCrashArtifactClaim.Missing,
            RecoveryCrashArtifactClaim.RegularFileObserved);
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            Frames(id, RecoveryNativeMutationKind.PublishPair, StartEvents()), snapshot);
        Assert.AreEqual(RecoveryCrashReconciliationDisposition.ManifestWithoutCopy,
            report.Disposition);
    }

    [TestMethod]
    public void FullPublishClaimsRemainUnverifiedWithBothFilesObserved()
    {
        Guid id = Guid.NewGuid();
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            Frames(id, RecoveryNativeMutationKind.PublishPair, PublishEvents()),
            BothClaimed);
        Assert.AreEqual(RecoveryCrashReconciliationDisposition.ClaimedCompleteUnverified,
            report.Disposition);
        Assert.AreEqual(RecoveryNativeMutationPhase.SequenceObserved,
            report.ClaimedPhase);
        Assert.IsFalse(report.CanAuthorizeRestoration);
    }

    [TestMethod]
    public void FullDeleteClaimsRemainUnverifiedWithBothFilesAbsent()
    {
        Guid id = Guid.NewGuid();
        var report = Analyze(id, RecoveryNativeMutationKind.DeletePair,
            Frames(id, RecoveryNativeMutationKind.DeletePair, DeleteEvents()),
            BothMissing);
        Assert.AreEqual(RecoveryCrashReconciliationDisposition.ClaimedCompleteUnverified,
            report.Disposition);
        Assert.IsFalse(report.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void CompletedDeleteClaimWithRemainingFilesIsAContradiction()
    {
        Guid id = Guid.NewGuid();
        var report = Analyze(id, RecoveryNativeMutationKind.DeletePair,
            Frames(id, RecoveryNativeMutationKind.DeletePair, DeleteEvents()), BothClaimed);
        Assert.AreEqual(
            RecoveryCrashReconciliationDisposition.JournalConflictsWithArtifactObservation,
            report.Disposition);
    }

    [TestMethod]
    public void CompletedPublishClaimWithMissingFilesIsAContradiction()
    {
        Guid id = Guid.NewGuid();
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            Frames(id, RecoveryNativeMutationKind.PublishPair, PublishEvents()), BothMissing);
        Assert.AreEqual(
            RecoveryCrashReconciliationDisposition.JournalConflictsWithArtifactObservation,
            report.Disposition);
    }

    [TestMethod]
    public void UntrustedLinkOrNonFileTakesPriorityOverOtherFindings()
    {
        Guid id = Guid.NewGuid();
        var snapshot = new RecoveryCrashArtifactSnapshot(
            RecoveryCrashArtifactClaim.UntrustedLinkOrNonFile,
            RecoveryCrashArtifactClaim.Missing);
        var report = Analyze(id, RecoveryNativeMutationKind.DeletePair,
            Frames(id, RecoveryNativeMutationKind.DeletePair, StartEvents()), snapshot);
        Assert.AreEqual(RecoveryCrashReconciliationDisposition.UntrustedArtifact,
            report.Disposition);
    }

    [TestMethod]
    public void UnknownAndInaccessibleMetadataCannotBeAssumedAbsent()
    {
        Guid id = Guid.NewGuid();
        var snapshot = new RecoveryCrashArtifactSnapshot(
            RecoveryCrashArtifactClaim.MetadataUnavailable,
            RecoveryCrashArtifactClaim.NotObserved);
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            Frames(id, RecoveryNativeMutationKind.PublishPair, StartEvents()), snapshot);
        Assert.AreEqual(RecoveryCrashReconciliationDisposition.MetadataUnavailable,
            report.Disposition);
    }

    [TestMethod]
    public void CrashAfterUnlinkIsPartialAndPreservesSurvivingManifest()
    {
        Guid id = Guid.NewGuid();
        var events = StartEvents().Concat(new[]
        {
            RecoveryNativeMutationEvent.RevalidateCopyName,
            RecoveryNativeMutationEvent.UnlinkCopyEntry,
            RecoveryNativeMutationEvent.ProcessInterrupted
        }).ToArray();
        var report = Analyze(id, RecoveryNativeMutationKind.DeletePair,
            Frames(id, RecoveryNativeMutationKind.DeletePair, events), BothMissing);
        Assert.AreEqual(RecoveryCrashReconciliationDisposition.PartialMutationClaim,
            report.Disposition);
        Assert.IsFalse(report.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void CancelBeforeAnyMutationIsNotPermissionToDeleteStaging()
    {
        Guid id = Guid.NewGuid();
        var events = StartEvents().Concat(new[]
        {
            RecoveryNativeMutationEvent.CancelRequested
        }).ToArray();
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            Frames(id, RecoveryNativeMutationKind.PublishPair, events), BothMissing);
        Assert.AreEqual(
            RecoveryCrashReconciliationDisposition.InterruptedBeforeMutationClaim,
            report.Disposition);
        Assert.IsFalse(report.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void HashChainDetectsSimpleMutationGapAndReordering()
    {
        Guid id = Guid.NewGuid();
        var frames = Frames(id, RecoveryNativeMutationKind.PublishPair,
            StartEvents());
        var tampered = (RecoveryCrashJournalFrame[])frames.Clone();
        tampered[1] = tampered[1] with
        {
            Event = RecoveryNativeMutationEvent.UnlinkManifestEntry
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.PublishPair, tampered, BothMissing));
        tampered = (RecoveryCrashJournalFrame[])frames.Clone();
        tampered[1] = tampered[1] with { Sequence = 5 };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.PublishPair, tampered, BothMissing));
        tampered = new[] { frames[1], frames[0], frames[2] };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.PublishPair, tampered, BothMissing));
    }

    [TestMethod]
    public void DifferentTransactionOrKindAndWrongHeadAreRejected()
    {
        Guid id = Guid.NewGuid();
        var frames = Frames(id, RecoveryNativeMutationKind.DeletePair, StartEvents());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(Guid.NewGuid(), RecoveryNativeMutationKind.DeletePair,
                frames, BothMissing));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.PublishPair,
                frames, BothMissing));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.DeletePair,
                frames, BothMissing, new string('A', 64)));
    }

    [TestMethod]
    public void TruncationIsDetectableOnlyWithExternalHeadAndNoAuthorityFollows()
    {
        Guid id = Guid.NewGuid();
        var frames = Frames(id, RecoveryNativeMutationKind.PublishPair,
            StartEvents());
        var prefix = frames.Take(2).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.PublishPair, prefix,
                BothMissing, frames[^1].FrameSha256));
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            prefix, BothMissing);
        Assert.AreEqual(
            RecoveryCrashReconciliationDisposition.InterruptedBeforeMutationClaim,
            report.Disposition);
        Assert.IsFalse(report.HasIndependentJournalAuthentication);
    }

    [TestMethod]
    public void InvalidJournalBudgetAndInvalidObservationsFailClosed()
    {
        Guid id = Guid.NewGuid();
        var synthetic = Frames(id, RecoveryNativeMutationKind.PublishPair,
            StartEvents());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.PublishPair,
                Enumerable.Repeat(synthetic[0], 17).ToArray(), BothMissing));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.PublishPair,
                synthetic, new RecoveryCrashArtifactSnapshot(
                    (RecoveryCrashArtifactClaim)999, RecoveryCrashArtifactClaim.Missing)));
    }

    [TestMethod]
    public void SyntheticHashIsStableButNotProofOfAuthenticity()
    {
        Guid id = Guid.NewGuid();
        var a = RecoveryCrashJournalReconciler.CreateSyntheticFrame(id,
            RecoveryNativeMutationKind.PublishPair, 1,
            RecoveryNativeMutationEvent.ObserveParentAnchor,
            RecoveryCrashJournalReconciler.GenesisSha256);
        var b = RecoveryCrashJournalReconciler.CreateSyntheticFrame(id,
            RecoveryNativeMutationKind.PublishPair, 1,
            RecoveryNativeMutationEvent.ObserveParentAnchor,
            RecoveryCrashJournalReconciler.GenesisSha256);
        Assert.AreEqual(a.FrameSha256, b.FrameSha256);
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            new[] { a }, BothMissing, a.FrameSha256);
        Assert.IsFalse(report.HasIndependentJournalAuthentication);
        Assert.IsFalse(report.CanAuthorizeRestoration);
        Assert.IsFalse(report.CanAuthorizePublication);
        Assert.IsFalse(report.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void CanonicalJournalBytesRoundTripAsCandidateOnly()
    {
        Guid id = Guid.NewGuid();
        var frames = Frames(id, RecoveryNativeMutationKind.PublishPair, StartEvents());
        byte[] encoded = RecoveryCrashJournalCodec.EncodeSynthetic(frames);
        var decoded = RecoveryCrashJournalCodec.DecodeCandidate(encoded);
        CollectionAssert.AreEqual(frames, decoded);
        var report = Analyze(id, RecoveryNativeMutationKind.PublishPair,
            decoded, BothMissing);
        Assert.IsFalse(report.HasIndependentJournalAuthentication);
        Assert.IsFalse(RecoveryCrashJournalCodec.CanAuthorizeFileMutation);
    }

    [TestMethod]
    public void DecoderRejectsNoncanonicalWhitespaceAndUnknownProperties()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalCodec.DecodeCandidate(
                System.Text.Encoding.UTF8.GetBytes(" []")));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalCodec.DecodeCandidate(
                System.Text.Encoding.UTF8.GetBytes("[{\"Unexpected\":true}]")));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalCodec.DecodeCandidate(
                System.Text.Encoding.UTF8.GetBytes("{not-json")));
    }

    [TestMethod]
    public void DecoderEnforcesByteSizeAndRecordCountLimits()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalCodec.DecodeCandidate(new byte[8193]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalCodec.DecodeCandidate(Array.Empty<byte>()));
        var id = Guid.NewGuid();
        var first = Frames(id, RecoveryNativeMutationKind.PublishPair, StartEvents())[0];
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryCrashJournalCodec.EncodeSynthetic(
                Enumerable.Repeat(first, 17).ToArray()));
    }

    [TestMethod]
    public void CanonicalJsonDoesNotMeanAuthenticOrSemanticallyValid()
    {
        Guid id = Guid.NewGuid();
        var frames = Frames(id, RecoveryNativeMutationKind.PublishPair, StartEvents());
        frames[0] = frames[0] with { Event = RecoveryNativeMutationEvent.UnlinkCopyEntry };
        byte[] encoded = RecoveryCrashJournalCodec.EncodeSynthetic(frames);
        var decoded = RecoveryCrashJournalCodec.DecodeCandidate(encoded);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.PublishPair, decoded, BothMissing));
        Assert.IsFalse(RecoveryCrashJournalCodec.CanAuthorizeFileMutation);
    }

    [TestMethod]
    public void EmptyCanonicalJournalIsNotEvidenceOfNoInterruptedWork()
    {
        byte[] encoded = RecoveryCrashJournalCodec.EncodeSynthetic(
            Array.Empty<RecoveryCrashJournalFrame>());
        var decoded = RecoveryCrashJournalCodec.DecodeCandidate(encoded);
        Assert.AreEqual(0, decoded.Length);
        var report = Analyze(Guid.NewGuid(), RecoveryNativeMutationKind.DeletePair,
            decoded, BothMissing);
        Assert.AreEqual(RecoveryCrashReconciliationDisposition.NoJournalEvidence,
            report.Disposition);
    }

    [TestMethod]
    public void UnknownEventOrTerminalReplayIsRejected()
    {
        Guid id = Guid.NewGuid();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RecoveryCrashJournalReconciler.CreateSyntheticFrame(
                id, RecoveryNativeMutationKind.PublishPair, 1,
                (RecoveryNativeMutationEvent)999,
                RecoveryCrashJournalReconciler.GenesisSha256));
        var events = StartEvents().Concat(new[]
        {
            RecoveryNativeMutationEvent.CancelRequested,
            RecoveryNativeMutationEvent.ObserveParentAnchor
        }).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Analyze(id, RecoveryNativeMutationKind.DeletePair,
                Frames(id, RecoveryNativeMutationKind.DeletePair, events), BothMissing));
    }
}
