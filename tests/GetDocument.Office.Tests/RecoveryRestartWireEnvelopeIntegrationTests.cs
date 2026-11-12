using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27ca / BLOCK 3: transport validation precedes the existing
/// denial-only restart reconciliation. Tests authored, not executed here.
/// </summary>
[TestClass]
public sealed class RecoveryRestartWireEnvelopeIntegrationTests
{
    private static readonly string PriorMac = new('0', 64);
    private static byte[] Key() =>
        Enumerable.Range(0, 32).Select(i => (byte)(i + 31)).ToArray();
    private static RecoveryNativeFileId Id(ulong inode) =>
        new(RecoveryNativePlatform.Linux, 23, inode, 0);
    private static RecoveryPairIdentityEvidence ClaimedIdentity() =>
        new(Id(1), Id(2), Id(3), Id(2), Id(3),
            ParentHandleHeld: true, NamesOpenedRelativeToParent: true,
            NoFollowEnforced: true, WriterIsolationConfirmed: true, LockHeld: true);

    private static (byte[] wire, RecoveryProtectedAnchorCandidate anchor) Valid(
        Guid transactionId)
    {
        byte[] journal = RecoveryCrashJournalCodec.EncodeSynthetic(
            Array.Empty<RecoveryCrashJournalFrame>());
        byte[] wire = RecoveryCrashJournalWireEnvelope.EncodeSynthetic(journal);
        var anchor = RecoveryProtectedAnchorProtocol.CreateSynthetic(
            transactionId, 9, RecoveryCrashJournalReconciler.GenesisSha256,
            PriorMac, Key());
        return (wire, anchor);
    }

    private static RecoveryRestartReviewResult Inspect(
        Guid tx, byte[] wire, RecoveryProtectedAnchorCandidate anchor,
        byte[]? key = null) =>
        RecoveryRestartCheckpointReview.InspectEnvelope(
            wire, anchor, key ?? Key(), tx, RecoveryNativeMutationKind.PublishPair,
            9, PriorMac, ClaimedIdentity(),
            new(RecoveryCrashArtifactClaim.Missing,
                RecoveryCrashArtifactClaim.Missing),
            default);

    [TestMethod]
    public void CompleteEnvelopeDoesNotTurnEmptyJournalIntoPermission()
    {
        Guid tx = Guid.NewGuid();
        var (wire, anchor) = Valid(tx);
        var result = Inspect(tx, wire, anchor);
        Assert.AreEqual(RecoveryRestartFinding.NoJournalEvidence, result.Finding);
        Assert.IsFalse(result.CanRestore);
        Assert.IsFalse(result.CanDelete);
        Assert.IsFalse(result.CanResumePublication);
    }

    [TestMethod]
    public void DamagedEnvelopeIsInvalidJournalNotSuccessfulRestart()
    {
        Guid tx = Guid.NewGuid();
        var (wire, anchor) = Valid(tx);
        wire[^1] ^= 0x04;
        var result = Inspect(tx, wire, anchor);
        Assert.AreEqual(RecoveryRestartFinding.InvalidJournal, result.Finding);
        Assert.IsFalse(result.ReleaseReady);
    }

    [TestMethod]
    public void WellFormedEnvelopeNeverOverridesWrongAnchorKey()
    {
        Guid tx = Guid.NewGuid();
        var (wire, anchor) = Valid(tx);
        byte[] wrongKey = Key();
        wrongKey[0] ^= 0x7F;
        Assert.AreEqual(RecoveryRestartFinding.AnchorRejected,
            Inspect(tx, wire, anchor, wrongKey).Finding);
    }

    [TestMethod]
    public void CancellationBeforeParsePropagatesWithoutRecovery()
    {
        Guid tx = Guid.NewGuid();
        var (wire, anchor) = Valid(tx);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            RecoveryRestartCheckpointReview.InspectEnvelope(
                wire, anchor, Key(), tx, RecoveryNativeMutationKind.PublishPair,
                9, PriorMac, ClaimedIdentity(),
                new(RecoveryCrashArtifactClaim.Missing,
                    RecoveryCrashArtifactClaim.Missing), default, cancel.Token));
    }
}
