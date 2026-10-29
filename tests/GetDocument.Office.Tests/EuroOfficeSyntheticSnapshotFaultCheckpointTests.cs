using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSyntheticSnapshotFaultCheckpointTests
{
    private sealed class ExpectedCrashException : Exception { }

    [TestMethod]
    public void EverySyntheticCrashBoundaryRemainsReadOnlyAndCannotClaimCompletion()
    {
        // These exceptions simulate process termination at exact write barriers.
        // They do NOT substitute for an OS crash, kill -9 or power-loss test.
        var cases = new[]
        {
            (EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterIntentPersisted,
                EuroOfficeSyntheticSnapshotJournal.Observation.NoOutputObserved,
                "intent", false, false),
            (EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterPendingFileFlushed,
                EuroOfficeSyntheticSnapshotJournal.Observation.PendingNeedsReview,
                "intent", true, false),
            (EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterPendingJournalFlushed,
                EuroOfficeSyntheticSnapshotJournal.Observation.PendingNeedsReview,
                "pending_flushed", true, false),
            (EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterHardLinkCreated,
                EuroOfficeSyntheticSnapshotJournal.Observation.PendingAfterLink,
                "pending_flushed", true, true),
            (EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterLinkedJournalFlushed,
                EuroOfficeSyntheticSnapshotJournal.Observation.PendingAfterLink,
                "linked", true, true),
            (EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterPendingLinkRemoved,
                EuroOfficeSyntheticSnapshotJournal.Observation.UncertainAfterLink,
                "linked", false, true),
            (EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterFinalHashVerified,
                EuroOfficeSyntheticSnapshotJournal.Observation.UncertainAfterLink,
                "linked", false, true),
            (EuroOfficeSyntheticSnapshotFaultCheckpoint.AfterCompletedJournalFlushed,
                EuroOfficeSyntheticSnapshotJournal.Observation.CompleteObserved,
                "completed", false, true)
        };

        foreach (var (checkpoint, expected, lastStage, hasPending, hasFinal) in cases)
        {
            using var lab = new EuroOfficeSyntheticReviewedSnapshotTests.SyntheticLab();
            lab.Create();
            var review = lab.GetReview();
            string sourceBefore = lab.Hash(lab.Source);
            string candidateBefore = lab.Hash(lab.Candidate);
            int calls = 0;
            Assert.ThrowsExactly<ExpectedCrashException>(() =>
                EuroOfficeSyntheticReviewedSnapshot.CommitWithFaultProbe(
                    lab.Root, review, point =>
                    {
                        if (point == checkpoint)
                        {
                            calls++;
                            throw new ExpectedCrashException();
                        }
                    }, labApproved: true));
            Assert.AreEqual(1, calls, $"Injection was not reached at {checkpoint}.");
            Assert.AreEqual(sourceBefore, lab.Hash(lab.Source));
            Assert.AreEqual(candidateBefore, lab.Hash(lab.Candidate));

            var observed = EuroOfficeSyntheticSnapshotJournal.Inspect(
                lab.Root, labApproved: true);
            Assert.AreEqual(1, observed.Count);
            Assert.AreEqual(expected, observed[0].State, checkpoint.ToString());
            Assert.AreEqual(lastStage, observed[0].LastStage);
            Assert.AreEqual(hasPending, Directory.GetFiles(
                lab.Reviewed, ".gd31j29-pending-*.tmp").Length == 1);
            Assert.AreEqual(hasFinal, File.Exists(Path.Combine(
                lab.Reviewed, EuroOfficeSyntheticReviewedSnapshot.ExpectedName(
                    review.Receipt.SessionId, review.Receipt.SelectedFormat))));
            Assert.IsFalse(observed[0].TrustedForDeletion);
            Assert.IsFalse(observed[0].AuthorizedForPublication);

            var report = EuroOfficeSyntheticRecoveryReport.Build(
                lab.Root, pageIndex: 0, labApproved: true);
            Assert.AreEqual(expected, report.Findings.Single().State);
            Assert.IsFalse(report.CleanupAuthorized);
            Assert.IsFalse(report.AuthorizedForPublication);
            Assert.IsFalse(report.DurabilityProven);
        }
    }

    [TestMethod]
    public void DisabledLabCannotReachFaultHookOrWriteAnyJournal()
    {
        using var lab = new EuroOfficeSyntheticReviewedSnapshotTests.SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        bool hookInvoked = false;
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.CommitWithFaultProbe(
                lab.Root, review, _ => hookInvoked = true, labApproved: false));
        Assert.IsFalse(hookInvoked);
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void NullHookOrUnapprovedRevokedEvidenceCannotGainWriteAuthority()
    {
        using var lab = new EuroOfficeSyntheticReviewedSnapshotTests.SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.CommitWithFaultProbe(
                lab.Root, review, null!, labApproved: true));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.CommitWithFaultProbe(
                lab.Root, review with { NativeEvidence = null },
                _ => throw new ExpectedCrashException(), labApproved: true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void SuccessfulCommitWithoutProbeStillWritesExactlyFourStages()
    {
        using var lab = new EuroOfficeSyntheticReviewedSnapshotTests.SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        var receipt = EuroOfficeSyntheticReviewedSnapshot.Commit(
            lab.Root, review, labApproved: true);
        Assert.IsFalse(receipt.SaveCompleted);
        Assert.IsFalse(receipt.AuthorizedForPublication);
        Assert.IsFalse(receipt.DurableAcrossPowerLoss);
        var row = EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, true).Single();
        Assert.AreEqual(EuroOfficeSyntheticSnapshotJournal.Observation.CompleteObserved,
            row.State);
        Assert.AreEqual("completed", row.LastStage);
        Assert.AreEqual(1, Directory.GetFiles(lab.Reviewed,
            ".gd31j30-intent-*.jsonl").Length);
    }
}
