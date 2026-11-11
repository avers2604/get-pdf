using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27cd: reconciliation of entirely caller-claimed journal and
/// manifest metadata. Does not read or authenticate actual document bytes.
/// </summary>
[TestClass]
public sealed class RecoveryScratchPairClaimReviewTests
{
    private static readonly string Hash = new('A', 64);
    private static readonly string Alternate = new('B', 64);

    private static RecoveryCrashReconciliationReport Journal(
        RecoveryCrashReconciliationDisposition disposition =
            RecoveryCrashReconciliationDisposition.ClaimedCompleteUnverified) =>
        new(disposition, 9, RecoveryNativeMutationPhase.SequenceObserved, Hash);

    private static RecoveryScratchManifestLinkClaim Linked() =>
        new(Guid.NewGuid(), Guid.Empty, Hash, Hash, Alternate, Alternate);

    private static RecoveryScratchManifestLinkClaim ValidLink()
    {
        var id = Guid.NewGuid();
        return new RecoveryScratchManifestLinkClaim(
            id, id, Hash, Hash, Alternate, Alternate);
    }

    private static RecoveryScratchPairReviewResult Evaluate(
        RecoveryScratchManifestLinkClaim link,
        RecoveryCrashReconciliationReport? journal = null,
        RecoveryCrashArtifactSnapshot? artifacts = null,
        RecoveryScratchNameClaim copyName = RecoveryScratchNameClaim.MatchedAtObservation,
        RecoveryScratchNameClaim manifestName = RecoveryScratchNameClaim.MatchedAtObservation) =>
        RecoveryScratchPairClaimReview.Evaluate(
            journal ?? Journal(), link,
            artifacts ?? new RecoveryCrashArtifactSnapshot(
                RecoveryCrashArtifactClaim.RegularFileObserved,
                RecoveryCrashArtifactClaim.RegularFileObserved),
            copyName, manifestName);

    [TestMethod]
    public void FullyMatchingClaimsAreNeverAuthorized()
    {
        var result = Evaluate(ValidLink());
        Assert.AreEqual(RecoveryScratchPairFinding.ClaimedConsistentUntrusted,
            result.Finding);
        Assert.IsFalse(result.CanRestore);
        Assert.IsFalse(result.CanDelete);
        Assert.IsFalse(result.CanPublish);
        Assert.IsFalse(result.HasAuthenticManifest);
    }

    [TestMethod]
    public void ManifestTransactionMismatchIsRejected()
    {
        var result = Evaluate(Linked());
        Assert.AreEqual(RecoveryScratchPairFinding.ManifestConflict,
            result.Finding);
    }

    [TestMethod]
    public void ManifestJournalHeadMismatchIsRejected()
    {
        var link = ValidLink() with { ManifestJournalHead = Alternate };
        Assert.AreEqual(RecoveryScratchPairFinding.ManifestConflict,
            Evaluate(link).Finding);
    }

    [TestMethod]
    public void JournalReplayHeadMismatchIsRejected()
    {
        var link = ValidLink() with { ExpectedJournalHead = Alternate };
        Assert.AreEqual(RecoveryScratchPairFinding.ManifestConflict,
            Evaluate(link).Finding);
    }

    [TestMethod]
    public void ManifestDeclaredCopyDigestMismatchIsRejected()
    {
        var link = ValidLink() with { ManifestCopySha256 = Hash };
        Assert.AreEqual(RecoveryScratchPairFinding.ManifestConflict,
            Evaluate(link).Finding);
    }

    [TestMethod]
    public void InvalidOrLowercaseDigestCannotBeCoerced()
    {
        Assert.AreEqual(RecoveryScratchPairFinding.InvalidEvidence,
            Evaluate(ValidLink() with { ObservedCopySha256 = "invalid" }).Finding);
        Assert.AreEqual(RecoveryScratchPairFinding.InvalidEvidence,
            Evaluate(ValidLink() with {
                ObservedCopySha256 = Alternate.ToLowerInvariant()
            }).Finding);
    }

    [TestMethod]
    public void MissingCopyOrManifestIsNeverRecovered()
    {
        Assert.AreEqual(RecoveryScratchPairFinding.MissingComponent,
            Evaluate(ValidLink(), artifacts: new(
                RecoveryCrashArtifactClaim.Missing,
                RecoveryCrashArtifactClaim.RegularFileObserved)).Finding);
        Assert.AreEqual(RecoveryScratchPairFinding.MissingComponent,
            Evaluate(ValidLink(), artifacts: new(
                RecoveryCrashArtifactClaim.RegularFileObserved,
                RecoveryCrashArtifactClaim.Missing)).Finding);
    }

    [TestMethod]
    public void SymlinkAndMetadataUnavailabilityRequireReview()
    {
        foreach (var code in new[]
        {
            RecoveryCrashArtifactClaim.UntrustedLinkOrNonFile,
            RecoveryCrashArtifactClaim.NotObserved,
            RecoveryCrashArtifactClaim.MetadataUnavailable
        })
            Assert.AreEqual(RecoveryScratchPairFinding.UntrustedArtifacts,
                Evaluate(ValidLink(), artifacts: new(
                    code, RecoveryCrashArtifactClaim.RegularFileObserved)).Finding);
    }

    [TestMethod]
    public void JournalIncompleteBlocksAllConsistencyClaims()
    {
        Assert.AreEqual(RecoveryScratchPairFinding.JournalIncomplete,
            Evaluate(ValidLink(), journal: Journal(
                RecoveryCrashReconciliationDisposition.PartialMutationClaim)).Finding);
        Assert.AreEqual(RecoveryScratchPairFinding.JournalIncomplete,
            Evaluate(ValidLink(), journal: Journal(
                RecoveryCrashReconciliationDisposition.NoJournalEvidence)).Finding);
    }

    [TestMethod]
    public void CopyAndManifestNameMustBothHaveObservation()
    {
        Assert.AreEqual(RecoveryScratchPairFinding.NameBindingUnverified,
            Evaluate(ValidLink(), copyName: RecoveryScratchNameClaim.NotObserved).Finding);
        Assert.AreEqual(RecoveryScratchPairFinding.NameBindingUnverified,
            Evaluate(ValidLink(), manifestName: RecoveryScratchNameClaim.Mismatch).Finding);
    }

    [TestMethod]
    public void ClaimedManifestLinkNeverAttestsContents()
    {
        Assert.IsFalse(RecoveryScratchPairClaimReview.NativeManifestReaderEnabled);
        Assert.IsFalse(RecoveryScratchPairClaimReview.CanAuthorizeMutation);
        Assert.IsFalse(Evaluate(ValidLink()).HasVerifiedOoxmlContent);
    }

    [TestMethod]
    public void UnknownStatusIsInvalidEvidence()
    {
        Assert.AreEqual(RecoveryScratchPairFinding.InvalidEvidence,
            Evaluate(ValidLink(), journal: Journal(
                (RecoveryCrashReconciliationDisposition)999)).Finding);
        Assert.AreEqual(RecoveryScratchPairFinding.InvalidEvidence,
            Evaluate(ValidLink(), copyName:
                (RecoveryScratchNameClaim)999).Finding);
    }
}
