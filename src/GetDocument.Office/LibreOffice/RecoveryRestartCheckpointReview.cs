namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bz / BLOCK 3: purely in-memory restart reconciliation.
/// Inputs are synthetic candidate journal/anchor bytes and caller-provided
/// read-only observations. This does NOT open handles or attest native state.
/// No cleanup, restore, publish, journal persistence or production wiring.
/// </summary>
internal static class RecoveryRestartCheckpointReview
{
    internal static bool NativeRestartAttestationEnabled => false;

    /// <summary>
    /// GD-31J.27ca / BLOCK 3. Parse a single bounded transport envelope
    /// before performing the existing in-memory anchor/journal reconciliation.
    /// This method does not touch storage, authorize mutation or attest fsync.
    /// </summary>
    internal static RecoveryRestartReviewResult InspectEnvelope(
        ReadOnlyMemory<byte> envelopeBytes,
        RecoveryProtectedAnchorCandidate anchor,
        ReadOnlySpan<byte> callerSuppliedKey,
        Guid expectedTransactionId,
        RecoveryNativeMutationKind kind,
        ulong expectedGeneration,
        string expectedPreviousAnchorMac,
        RecoveryPairIdentityEvidence claimedPairIdentity,
        RecoveryCrashArtifactSnapshot artifacts,
        RecoveryRestartFileIdSnapshot reopenedIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] journal;
        try
        {
            journal = RecoveryCrashJournalWireEnvelope.DecodeCandidate(
                envelopeBytes.Span);
        }
        catch (InvalidDataException)
        {
            return new RecoveryRestartReviewResult(
                RecoveryRestartFinding.InvalidJournal,
                RecoveryCrashReconciliationDisposition.NoJournalEvidence);
        }

        return Inspect(journal, anchor, callerSuppliedKey,
            expectedTransactionId, kind, expectedGeneration,
            expectedPreviousAnchorMac, claimedPairIdentity, artifacts,
            reopenedIds, cancellationToken);
    }

    internal static RecoveryRestartReviewResult Inspect(
        ReadOnlyMemory<byte> journalBytes,
        RecoveryProtectedAnchorCandidate anchor,
        ReadOnlySpan<byte> callerSuppliedKey,
        Guid expectedTransactionId,
        RecoveryNativeMutationKind kind,
        ulong expectedGeneration,
        string expectedPreviousAnchorMac,
        RecoveryPairIdentityEvidence claimedPairIdentity,
        RecoveryCrashArtifactSnapshot artifacts,
        RecoveryRestartFileIdSnapshot reopenedIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RecoveryAnchorAssessment anchorAssessment;
        try
        {
            // A matching MAC proves consistency with THIS caller-supplied
            // key only. No trusted key service or rollback-resistant counter.
            anchorAssessment = RecoveryProtectedAnchorProtocol.AssessCandidate(
                anchor, callerSuppliedKey, expectedTransactionId,
                expectedGeneration, anchor.JournalHeadSha256,
                expectedPreviousAnchorMac);
        }
        catch (InvalidDataException)
        {
            return new RecoveryRestartReviewResult(
                RecoveryRestartFinding.AnchorRejected,
                RecoveryCrashReconciliationDisposition.NoJournalEvidence);
        }

        if (anchorAssessment.Finding != RecoveryAnchorFinding.MatchedButUntrusted)
            return new RecoveryRestartReviewResult(
                RecoveryRestartFinding.AnchorRejected,
                RecoveryCrashReconciliationDisposition.NoJournalEvidence);

        cancellationToken.ThrowIfCancellationRequested();

        RecoveryCrashReconciliationReport journalReport;
        try
        {
            // Neither the checksum nor a synthetic OS-identity statement
            // proves that the underlying file survived a real system crash.
            RecoveryCrashJournalFrame[] frames =
                RecoveryCrashJournalCodec.DecodeCandidate(journalBytes);
            journalReport = RecoveryCrashJournalReconciler.Analyze(
                frames, expectedTransactionId, kind, claimedPairIdentity,
                artifacts, anchor.JournalHeadSha256);
        }
        catch (InvalidDataException)
        {
            return new RecoveryRestartReviewResult(
                RecoveryRestartFinding.InvalidJournal,
                RecoveryCrashReconciliationDisposition.NoJournalEvidence);
        }

        cancellationToken.ThrowIfCancellationRequested();

        RecoveryRestartFinding finding = journalReport.Disposition switch
        {
            RecoveryCrashReconciliationDisposition.UntrustedArtifact or
            RecoveryCrashReconciliationDisposition.MetadataUnavailable =>
                RecoveryRestartFinding.NeedsNativeReadback,

            RecoveryCrashReconciliationDisposition.JournalConflictsWithArtifactObservation =>
                RecoveryRestartFinding.JournalArtifactConflict,

            RecoveryCrashReconciliationDisposition.CopyWithoutManifest or
            RecoveryCrashReconciliationDisposition.ManifestWithoutCopy =>
                RecoveryRestartFinding.OrphanRequiresReview,

            RecoveryCrashReconciliationDisposition.NoJournalEvidence =>
                RecoveryRestartFinding.NoJournalEvidence,

            RecoveryCrashReconciliationDisposition.InterruptedBeforeMutationClaim or
            RecoveryCrashReconciliationDisposition.PartialMutationClaim =>
                RecoveryRestartFinding.IncompleteOperation,

            RecoveryCrashReconciliationDisposition.ClaimedCompleteUnverified =>
                RecoveryRestartFinding.MatchedClaimsOnly,

            _ => RecoveryRestartFinding.NeedsNativeReadback
        };

        if (artifacts.Copy == RecoveryCrashArtifactClaim.RegularFileObserved &&
            artifacts.Manifest == RecoveryCrashArtifactClaim.RegularFileObserved &&
            finding is not (RecoveryRestartFinding.JournalArtifactConflict or
                            RecoveryRestartFinding.NeedsNativeReadback) &&
            !reopenedIds.HasStableDistinctFileIds())
        {
            finding = RecoveryRestartFinding.ReopenedIdentityMismatch;
        }

        return new RecoveryRestartReviewResult(finding, journalReport.Disposition);
    }
}

internal enum RecoveryRestartFinding
{
    AnchorRejected,
    InvalidJournal,
    NeedsNativeReadback,
    ReopenedIdentityMismatch,
    JournalArtifactConflict,
    OrphanRequiresReview,
    NoJournalEvidence,
    IncompleteOperation,
    MatchedClaimsOnly
}

/// <summary>
/// Synthetic native IDs from caller. Even matching IDs do NOT prove the
/// directory entry currently binds to those objects, or that the native
/// handle survived interruption. No name-based mutation is possible.
/// </summary>
internal readonly record struct RecoveryRestartFileIdSnapshot(
    RecoveryNativeFileId ParentAtOpen,
    RecoveryNativeFileId ParentAtRecheck,
    RecoveryNativeFileId CopyAtOpen,
    RecoveryNativeFileId CopyAtRecheck,
    RecoveryNativeFileId ManifestAtOpen,
    RecoveryNativeFileId ManifestAtRecheck)
{
    internal bool HasStableDistinctFileIds()
    {
        if (!ParentAtOpen.IsComplete ||
            !CopyAtOpen.IsComplete || !ManifestAtOpen.IsComplete ||
            ParentAtOpen != ParentAtRecheck ||
            CopyAtOpen != CopyAtRecheck ||
            ManifestAtOpen != ManifestAtRecheck)
            return false;
        if (ParentAtOpen.Platform != CopyAtOpen.Platform ||
            ParentAtOpen.Platform != ManifestAtOpen.Platform ||
            ParentAtOpen.DeviceOrVolume != CopyAtOpen.DeviceOrVolume ||
            ParentAtOpen.DeviceOrVolume != ManifestAtOpen.DeviceOrVolume)
            return false;
        return ParentAtOpen != CopyAtOpen &&
               ParentAtOpen != ManifestAtOpen &&
               CopyAtOpen != ManifestAtOpen;
    }
}

internal readonly record struct RecoveryRestartReviewResult(
    RecoveryRestartFinding Finding,
    RecoveryCrashReconciliationDisposition JournalDisposition)
{
    internal bool HasTrustedPersistentAnchor => false;
    internal bool HasIndependentlyAuthenticatedJournal => false;
    internal bool CanRestore => false;
    internal bool CanDelete => false;
    internal bool CanResumePublication => false;
    internal bool ReleaseReady => false;
}
