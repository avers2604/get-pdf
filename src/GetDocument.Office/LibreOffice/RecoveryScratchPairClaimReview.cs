namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27cd: pure denial-only correlation of supplied journal replay,
/// manifest claims, nominal copy checksum and synthetic name observations.
/// THIS DOES NOT READ actual manifest/copy/journal files and cannot prove
/// SHA, transaction origin, durable storage or OOXML/WORM authenticity.
/// Every field is caller-controlled until independent native attestation.
/// </summary>
internal static class RecoveryScratchPairClaimReview
{
    internal static bool NativeManifestReaderEnabled => false;
    internal static bool CanAuthorizeMutation => false;

    internal static RecoveryScratchPairReviewResult Evaluate(
        RecoveryCrashReconciliationReport journal,
        RecoveryScratchManifestLinkClaim link,
        RecoveryCrashArtifactSnapshot artifacts,
        RecoveryScratchNameClaim copyName,
        RecoveryScratchNameClaim manifestName)
    {
        if (!Enum.IsDefined(journal.Disposition) ||
            !Enum.IsDefined(artifacts.Copy) ||
            !Enum.IsDefined(artifacts.Manifest) ||
            !Enum.IsDefined(copyName) || !Enum.IsDefined(manifestName) ||
            link.ExpectedTransactionId == Guid.Empty ||
            !CanonicalSha(link.ExpectedJournalHead) ||
            !CanonicalSha(link.ManifestJournalHead) ||
            !CanonicalSha(link.ObservedCopySha256) ||
            !CanonicalSha(link.ManifestCopySha256) ||
            !CanonicalSha(journal.ClaimedJournalHeadSha256))
            return new RecoveryScratchPairReviewResult(
                RecoveryScratchPairFinding.InvalidEvidence);

        if (artifacts.Copy is RecoveryCrashArtifactClaim.UntrustedLinkOrNonFile
                or RecoveryCrashArtifactClaim.MetadataUnavailable
                or RecoveryCrashArtifactClaim.NotObserved ||
            artifacts.Manifest is RecoveryCrashArtifactClaim.UntrustedLinkOrNonFile
                or RecoveryCrashArtifactClaim.MetadataUnavailable
                or RecoveryCrashArtifactClaim.NotObserved)
            return new RecoveryScratchPairReviewResult(
                RecoveryScratchPairFinding.UntrustedArtifacts);

        if (artifacts.Copy == RecoveryCrashArtifactClaim.Missing ||
            artifacts.Manifest == RecoveryCrashArtifactClaim.Missing)
            return new RecoveryScratchPairReviewResult(
                RecoveryScratchPairFinding.MissingComponent);

        if (journal.Disposition !=
                RecoveryCrashReconciliationDisposition.ClaimedCompleteUnverified)
            return new RecoveryScratchPairReviewResult(
                RecoveryScratchPairFinding.JournalIncomplete);

        if (copyName != RecoveryScratchNameClaim.MatchedAtObservation ||
            manifestName != RecoveryScratchNameClaim.MatchedAtObservation)
            return new RecoveryScratchPairReviewResult(
                RecoveryScratchPairFinding.NameBindingUnverified);

        if (link.ExpectedTransactionId != link.ManifestTransactionId ||
            !string.Equals(link.ExpectedJournalHead, link.ManifestJournalHead,
                StringComparison.Ordinal) ||
            !string.Equals(link.ExpectedJournalHead, journal.ClaimedJournalHeadSha256,
                StringComparison.Ordinal) ||
            !string.Equals(link.ObservedCopySha256, link.ManifestCopySha256,
                StringComparison.Ordinal))
            return new RecoveryScratchPairReviewResult(
                RecoveryScratchPairFinding.ManifestConflict);

        return new RecoveryScratchPairReviewResult(
            RecoveryScratchPairFinding.ClaimedConsistentUntrusted);
    }

    private static bool CanonicalSha(string? value) =>
        value is { Length: 64 } &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal enum RecoveryScratchPairFinding
{
    InvalidEvidence,
    UntrustedArtifacts,
    MissingComponent,
    JournalIncomplete,
    NameBindingUnverified,
    ManifestConflict,
    ClaimedConsistentUntrusted
}

internal enum RecoveryScratchNameClaim
{
    NotObserved,
    Mismatch,
    MatchedAtObservation
}

internal readonly record struct RecoveryScratchManifestLinkClaim(
    Guid ExpectedTransactionId,
    Guid ManifestTransactionId,
    string ExpectedJournalHead,
    string ManifestJournalHead,
    string ObservedCopySha256,
    string ManifestCopySha256);

internal readonly record struct RecoveryScratchPairReviewResult(
    RecoveryScratchPairFinding Finding)
{
    internal bool CanRestore => false;
    internal bool CanDelete => false;
    internal bool CanPublish => false;
    internal bool HasAuthenticManifest => false;
    internal bool HasVerifiedOoxmlContent => false;
}
