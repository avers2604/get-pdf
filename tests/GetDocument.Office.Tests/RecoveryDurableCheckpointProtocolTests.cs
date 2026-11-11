using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class RecoveryDurableCheckpointProtocolTests
{
    private static readonly string Hash = new('A', 64);
    private static RecoveryDurableCheckpointPlan Start() =>
        RecoveryDurableCheckpointProtocol.Start(Guid.NewGuid(), 6, 7, Hash, 3);

    private static RecoveryDurableCheckpointPlan Replay(
        RecoveryDurableCheckpointPlan plan, params RecoveryDurableCheckpointEvent[] actions)
    {
        foreach (var action in actions)
            plan = RecoveryDurableCheckpointProtocol.Advance(plan, action);
        return plan;
    }

    [TestMethod]
    public void JournalThenAnchorBothRequireFileAndDirectorySync()
    {
        var plan = Replay(Start(),
            RecoveryDurableCheckpointEvent.JournalFileFlushClaimed,
            RecoveryDurableCheckpointEvent.JournalDirectorySyncClaimed,
            RecoveryDurableCheckpointEvent.AnchorFileFlushClaimed,
            RecoveryDurableCheckpointEvent.AnchorDirectorySyncClaimed,
            RecoveryDurableCheckpointEvent.RestartReadbackClaimed);
        Assert.AreEqual(RecoveryDurableCheckpointPhase.ReadbackClaimed, plan.Phase);
        Assert.AreEqual(5, plan.ObservedSteps);
        Assert.IsFalse(plan.CanAuthorizeMutation);
        Assert.IsFalse(plan.HasIndependentlyVerifiedDurability);
    }

    [TestMethod]
    public void DirectorySyncCannotPrecedeJournalContentFlush()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Advance(Start(),
                RecoveryDurableCheckpointEvent.JournalDirectorySyncClaimed));
    }

    [TestMethod]
    public void AnchorCannotPrecedeJournalDirectorySync()
    {
        var afterJournal = RecoveryDurableCheckpointProtocol.Advance(Start(),
            RecoveryDurableCheckpointEvent.JournalFileFlushClaimed);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Advance(afterJournal,
                RecoveryDurableCheckpointEvent.AnchorFileFlushClaimed));
    }

    [TestMethod]
    public void ReadbackCannotPrecedeAnchorDirectorySync()
    {
        var plan = Replay(Start(),
            RecoveryDurableCheckpointEvent.JournalFileFlushClaimed,
            RecoveryDurableCheckpointEvent.JournalDirectorySyncClaimed,
            RecoveryDurableCheckpointEvent.AnchorFileFlushClaimed);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Advance(plan,
                RecoveryDurableCheckpointEvent.RestartReadbackClaimed));
    }

    [TestMethod]
    public void CancellationBeforeFirstFlushIsSafeToSuspendButNotClean()
    {
        var plan = RecoveryDurableCheckpointProtocol.Advance(Start(),
            RecoveryDurableCheckpointEvent.CancelRequested);
        Assert.AreEqual(RecoveryDurableCheckpointPhase.SuspendedWithoutWriteClaim,
            plan.Phase);
        Assert.IsFalse(plan.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void CancellationAfterAnyFlushRequiresManualReview()
    {
        var plan = RecoveryDurableCheckpointProtocol.Advance(Start(),
            RecoveryDurableCheckpointEvent.JournalFileFlushClaimed);
        plan = RecoveryDurableCheckpointProtocol.Advance(plan,
            RecoveryDurableCheckpointEvent.CancelRequested);
        Assert.AreEqual(RecoveryDurableCheckpointPhase.RequiresManualReview, plan.Phase);
    }

    [TestMethod]
    public void PowerLossAfterAnchorFileFlushRequiresReview()
    {
        var plan = Replay(Start(),
            RecoveryDurableCheckpointEvent.JournalFileFlushClaimed,
            RecoveryDurableCheckpointEvent.JournalDirectorySyncClaimed,
            RecoveryDurableCheckpointEvent.AnchorFileFlushClaimed,
            RecoveryDurableCheckpointEvent.ProcessInterrupted);
        Assert.AreEqual(RecoveryDurableCheckpointPhase.RequiresManualReview, plan.Phase);
    }

    [TestMethod]
    public void LockLostAndMediaErrorAlwaysFailClosed()
    {
        foreach (var ev in new[]
        {
            RecoveryDurableCheckpointEvent.LockLost,
            RecoveryDurableCheckpointEvent.MediaError
        })
            Assert.AreEqual(RecoveryDurableCheckpointPhase.RequiresManualReview,
                RecoveryDurableCheckpointProtocol.Advance(Start(), ev).Phase);
    }

    [TestMethod]
    public void RejectsRollbackGenerationGapAndInvalidHead()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Start(Guid.NewGuid(), 7, 7, Hash, 3));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Start(Guid.NewGuid(), 6, 8, Hash, 3));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Start(Guid.NewGuid(), 6, 7, "abc", 3));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryDurableCheckpointProtocol.Start(Guid.Empty, 6, 7, Hash, 3));
    }

    [TestMethod]
    public void RejectsInvalidFrameBudgetAndOverflow()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Start(Guid.NewGuid(), 0, 1, Hash, 17));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Start(Guid.NewGuid(), 0, 1, Hash, 0));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Start(Guid.NewGuid(), ulong.MaxValue, 1, Hash, 3));
    }

    [TestMethod]
    public void RejectsReplayAndForgedStepCounter()
    {
        var plan = RecoveryDurableCheckpointProtocol.Advance(Start(),
            RecoveryDurableCheckpointEvent.JournalFileFlushClaimed);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Advance(plan,
                RecoveryDurableCheckpointEvent.JournalFileFlushClaimed));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Advance(plan with { ObservedSteps = 0 },
                RecoveryDurableCheckpointEvent.ProcessInterrupted));
    }

    [TestMethod]
    public void TerminalStateCannotBecomeAuthorized()
    {
        var p = RecoveryDurableCheckpointProtocol.Advance(Start(),
            RecoveryDurableCheckpointEvent.LockLost);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryDurableCheckpointProtocol.Advance(p,
                RecoveryDurableCheckpointEvent.JournalFileFlushClaimed));
        Assert.IsFalse(p.CanAuthorizeMutation);
        Assert.IsFalse(RecoveryDurableCheckpointProtocol.NativeStoreEnabled);
    }
}
