using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class RecoveryProtectedAnchorCandidateTests
{
    private static readonly string JournalHead = new('A', 64);
    private static readonly string PreviousTag = new('0', 64);
    private static byte[] Key() => Enumerable.Range(0, 32).Select(i => (byte)(i + 17)).ToArray();

    private static RecoveryProtectedAnchorCandidate Candidate(
        Guid tx, ulong generation = 9) =>
        RecoveryProtectedAnchorProtocol.CreateSynthetic(
            tx, generation, JournalHead, PreviousTag, Key());

    private static RecoveryAnchorAssessment Assess(
        RecoveryProtectedAnchorCandidate anchor, Guid tx, byte[]? key = null,
        ulong expectedGeneration = 9, string? expectedPrevious = null,
        string? expectedJournalHead = null) =>
        RecoveryProtectedAnchorProtocol.AssessCandidate(anchor, key ?? Key(),
            tx, expectedGeneration, expectedJournalHead ?? JournalHead,
            expectedPrevious ?? PreviousTag);

    [TestMethod]
    public void MatchingKeyedTagOnlyProvidesManualComparison()
    {
        Guid tx = Guid.NewGuid();
        var result = Assess(Candidate(tx), tx);
        Assert.AreEqual(RecoveryAnchorFinding.MatchedButUntrusted, result.Finding);
        Assert.IsTrue(result.TagMatchedWithSuppliedKey);
        Assert.IsFalse(result.HasIndependentlyTrustedKey);
        Assert.IsFalse(result.CanAuthorizeRecovery);
    }

    [TestMethod]
    public void WrongKeyRefusesTag()
    {
        Guid tx = Guid.NewGuid();
        byte[] changed = Key();
        changed[0] ^= 0x55;
        Assert.AreEqual(RecoveryAnchorFinding.TagMismatch,
            Assess(Candidate(tx), tx, changed).Finding);
    }

    [TestMethod]
    public void ReplayedOldGenerationIsRefused()
    {
        Guid tx = Guid.NewGuid();
        Assert.AreEqual(RecoveryAnchorFinding.GenerationMismatch,
            Assess(Candidate(tx, generation: 8), tx).Finding);
    }

    [TestMethod]
    public void AheadGenerationIsAlsoNotAccepted()
    {
        Guid tx = Guid.NewGuid();
        Assert.AreEqual(RecoveryAnchorFinding.GenerationMismatch,
            Assess(Candidate(tx, generation: 10), tx).Finding);
    }

    [TestMethod]
    public void WrongExpectedTransactionIsRejected()
    {
        Guid tx = Guid.NewGuid();
        Assert.AreEqual(RecoveryAnchorFinding.TransactionMismatch,
            Assess(Candidate(tx), Guid.NewGuid()).Finding);
    }

    [TestMethod]
    public void WrongJournalHeadIsRejectedBeforeTagUse()
    {
        Guid tx = Guid.NewGuid();
        Assert.AreEqual(RecoveryAnchorFinding.JournalHeadMismatch,
            Assess(Candidate(tx), tx, expectedJournalHead: new string('B', 64)).Finding);
    }

    [TestMethod]
    public void PriorTagMismatchDetectsBrokenLineageClaim()
    {
        Guid tx = Guid.NewGuid();
        Assert.AreEqual(RecoveryAnchorFinding.PreviousTagMismatch,
            Assess(Candidate(tx), tx, expectedPrevious: new string('B', 64)).Finding);
    }

    [TestMethod]
    public void TamperingCandidateFieldWithoutRekeyingFailsMac()
    {
        Guid tx = Guid.NewGuid();
        var anchor = Candidate(tx) with { JournalHeadSha256 = new string('B', 64) };
        Assert.AreEqual(RecoveryAnchorFinding.TagMismatch,
            Assess(anchor, tx, expectedJournalHead: new string('B', 64)).Finding);
    }

    [TestMethod]
    public void InvalidAndShortKeysFailClosed()
    {
        Guid tx = Guid.NewGuid();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RecoveryProtectedAnchorProtocol.CreateSynthetic(tx, 9,
                JournalHead, PreviousTag, new byte[16]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RecoveryProtectedAnchorProtocol.AssessCandidate(Candidate(tx),
                new byte[4], tx, 9, JournalHead, PreviousTag));
    }

    [TestMethod]
    public void MalformedTagAndNoncanonicalHashesFailClosed()
    {
        Guid tx = Guid.NewGuid();
        var good = Candidate(tx);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Assess(good with { MacSha256 = "bad" }, tx));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryProtectedAnchorProtocol.CreateSynthetic(
                tx, 9, JournalHead.ToLowerInvariant(), PreviousTag, Key()));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryProtectedAnchorProtocol.CreateSynthetic(
                tx, 9, JournalHead, PreviousTag.ToLowerInvariant().Replace('0', 'a'), Key()));
    }

    [TestMethod]
    public void IdenticalInputYieldsDeterministicTagButNotTrust()
    {
        Guid tx = Guid.NewGuid();
        var first = Candidate(tx);
        var second = Candidate(tx);
        Assert.AreEqual(first.MacSha256, second.MacSha256);
        Assert.IsFalse(RecoveryProtectedAnchorProtocol.NativeTrustStoreConnected);
    }

    [TestMethod]
    public void DefaultOrWrongVersionAnchorIsRefused()
    {
        Guid tx = Guid.NewGuid();
        var good = Candidate(tx);
        Assert.AreEqual(RecoveryAnchorFinding.VersionMismatch,
            Assess(good with { Version = 2 }, tx).Finding);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryProtectedAnchorProtocol.AssessCandidate(
                default, Key(), tx, 9, JournalHead, PreviousTag));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryProtectedAnchorProtocol.CreateSynthetic(
                Guid.Empty, 9, JournalHead, PreviousTag, Key()));
    }
}
