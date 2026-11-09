using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>GD-31J.27bx synthetic event model; no native operations.</summary>
[TestClass]
public sealed class RecoveryNativeMutationSequenceTests
{
    private static RecoveryNativeFileId File(ulong id) =>
        new(RecoveryNativePlatform.Linux, 88, id, 0);

    private static RecoveryPairIdentityEvidence Plausible() =>
        new(File(1), File(2), File(3), File(2), File(3),
            ParentHandleHeld: true, NamesOpenedRelativeToParent: true,
            NoFollowEnforced: true, WriterIsolationConfirmed: true, LockHeld: true);

    private static RecoveryNativeMutationPlan Start(RecoveryNativeMutationKind kind) =>
        RecoveryNativeMutationPlanner.Start(kind, Plausible(), Guid.NewGuid());

    private static RecoveryNativeMutationPlan Apply(
        RecoveryNativeMutationPlan plan, params RecoveryNativeMutationEvent[] events)
    {
        foreach (var action in events)
            plan = RecoveryNativeMutationPlanner.Advance(plan, action);
        return plan;
    }

    private static RecoveryNativeMutationEvent[] Initial() =>
        new[]
        {
            RecoveryNativeMutationEvent.ObserveParentAnchor,
            RecoveryNativeMutationEvent.ObserveExclusiveLock,
            RecoveryNativeMutationEvent.ObservePairNameAndIdentity
        };

    [TestMethod]
    public void PublishRequiresCopyThenManifestAndTwoDirectorySyncClaims()
    {
        var plan = Apply(Start(RecoveryNativeMutationKind.PublishPair),
            Initial().Concat(new[]
            {
                RecoveryNativeMutationEvent.FlushCopyContent,
                RecoveryNativeMutationEvent.RevalidateCopyName,
                RecoveryNativeMutationEvent.PublishCopyNoReplace,
                RecoveryNativeMutationEvent.SyncParentDirectory,
                RecoveryNativeMutationEvent.FlushManifestContent,
                RecoveryNativeMutationEvent.RevalidateManifestName,
                RecoveryNativeMutationEvent.PublishManifestNoReplace,
                RecoveryNativeMutationEvent.SyncParentDirectory
            }).ToArray());
        Assert.AreEqual(RecoveryNativeMutationPhase.SequenceObserved, plan.Phase);
        Assert.IsFalse(plan.CanAuthorizeNativeMutation);
        Assert.IsFalse(plan.ReleaseReady);
    }

    [TestMethod]
    public void DeleteRequiresCopyBeforeManifestAndSyncAfterEachUnlink()
    {
        var plan = Apply(Start(RecoveryNativeMutationKind.DeletePair),
            Initial().Concat(new[]
            {
                RecoveryNativeMutationEvent.RevalidateCopyName,
                RecoveryNativeMutationEvent.UnlinkCopyEntry,
                RecoveryNativeMutationEvent.SyncParentDirectory,
                RecoveryNativeMutationEvent.RevalidateManifestName,
                RecoveryNativeMutationEvent.UnlinkManifestEntry,
                RecoveryNativeMutationEvent.SyncParentDirectory
            }).ToArray());
        Assert.AreEqual(RecoveryNativeMutationPhase.SequenceObserved, plan.Phase);
        Assert.IsFalse(plan.CanAuthorizeNativeMutation);
    }

    [TestMethod]
    public void CannotPublishManifestBeforeCopyIsDurable()
    {
        var plan = Apply(Start(RecoveryNativeMutationKind.PublishPair), Initial());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                plan, RecoveryNativeMutationEvent.PublishManifestNoReplace));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                plan, RecoveryNativeMutationEvent.PublishCopyNoReplace));
    }

    [TestMethod]
    public void CannotDeleteManifestBeforeCopyAndDirectorySync()
    {
        var plan = Apply(Start(RecoveryNativeMutationKind.DeletePair), Initial());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                plan, RecoveryNativeMutationEvent.UnlinkManifestEntry));
        plan = Apply(plan, RecoveryNativeMutationEvent.RevalidateCopyName,
            RecoveryNativeMutationEvent.UnlinkCopyEntry);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                plan, RecoveryNativeMutationEvent.RevalidateManifestName));
    }

    [TestMethod]
    public void CancellationBeforeMutationPreservesUncommittedArtifacts()
    {
        var plan = Apply(Start(RecoveryNativeMutationKind.PublishPair),
            Initial().Concat(new[]
            {
                RecoveryNativeMutationEvent.FlushCopyContent,
                RecoveryNativeMutationEvent.RevalidateCopyName
            }).ToArray());
        plan = RecoveryNativeMutationPlanner.Advance(
            plan, RecoveryNativeMutationEvent.CancelRequested);
        Assert.AreEqual(RecoveryNativeMutationPhase.SuspendedWithoutMutation, plan.Phase);
        Assert.IsFalse(plan.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void CancellationAfterCopyPublishRequestsManualReviewNotRollback()
    {
        var plan = Apply(Start(RecoveryNativeMutationKind.PublishPair),
            Initial().Concat(new[]
            {
                RecoveryNativeMutationEvent.FlushCopyContent,
                RecoveryNativeMutationEvent.RevalidateCopyName,
                RecoveryNativeMutationEvent.PublishCopyNoReplace
            }).ToArray());
        plan = RecoveryNativeMutationPlanner.Advance(
            plan, RecoveryNativeMutationEvent.CancelRequested);
        Assert.AreEqual(RecoveryNativeMutationPhase.RequiresManualReview, plan.Phase);
        Assert.IsFalse(plan.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void CrashAfterCopyUnlinkCannotAuthorizeDeletingManifest()
    {
        var plan = Apply(Start(RecoveryNativeMutationKind.DeletePair),
            Initial().Concat(new[]
            {
                RecoveryNativeMutationEvent.RevalidateCopyName,
                RecoveryNativeMutationEvent.UnlinkCopyEntry
            }).ToArray());
        plan = RecoveryNativeMutationPlanner.Advance(
            plan, RecoveryNativeMutationEvent.ProcessInterrupted);
        Assert.AreEqual(RecoveryNativeMutationPhase.RequiresManualReview, plan.Phase);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                plan, RecoveryNativeMutationEvent.UnlinkManifestEntry));
    }

    [TestMethod]
    public void CrashBeforeNativeMutationCannotAuthorizeCleanupEither()
    {
        var plan = RecoveryNativeMutationPlanner.Advance(
            Start(RecoveryNativeMutationKind.DeletePair),
            RecoveryNativeMutationEvent.ProcessInterrupted);
        Assert.AreEqual(RecoveryNativeMutationPhase.SuspendedWithoutMutation, plan.Phase);
        Assert.IsFalse(plan.CanAuthorizeCleanup);
    }

    [TestMethod]
    public void LockLossFailsClosedEvenBeforeFirstMutation()
    {
        var plan = RecoveryNativeMutationPlanner.Advance(
            Start(RecoveryNativeMutationKind.PublishPair),
            RecoveryNativeMutationEvent.ExclusiveLockLost);
        Assert.AreEqual(RecoveryNativeMutationPhase.RequiresManualReview, plan.Phase);
    }

    [TestMethod]
    public void DuplicateEventsAndTerminalTransitionsAreRefused()
    {
        var plan = RecoveryNativeMutationPlanner.Advance(
            Start(RecoveryNativeMutationKind.DeletePair),
            RecoveryNativeMutationEvent.ObserveParentAnchor);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                plan, RecoveryNativeMutationEvent.ObserveParentAnchor));
        plan = RecoveryNativeMutationPlanner.Advance(
            plan, RecoveryNativeMutationEvent.CancelRequested);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                plan, RecoveryNativeMutationEvent.ObserveExclusiveLock));
    }

    [TestMethod]
    public void SyntheticIdentityClaimsCannotAuthorizeNativeMutation()
    {
        var plan = Start(RecoveryNativeMutationKind.PublishPair);
        Assert.IsFalse(plan.CanAuthorizeNativeMutation);
        Assert.IsFalse(plan.CanAuthorizeCleanup);
        Assert.IsFalse(plan.ReleaseReady);
        Assert.IsFalse(RecoveryNativeMutationPlanner.NativeBackendEnabled);
    }

    [TestMethod]
    public void IncompleteMismatchedOrMissingLockIdentityIsRejectedAtStart()
    {
        var broken = Plausible() with { CopyAtRecheck = File(77) };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Start(
                RecoveryNativeMutationKind.DeletePair, broken, Guid.NewGuid()));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Start(
                RecoveryNativeMutationKind.PublishPair,
                Plausible() with { LockHeld = false }, Guid.NewGuid()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryNativeMutationPlanner.Start(
                RecoveryNativeMutationKind.PublishPair, Plausible(), Guid.Empty));
    }

    [TestMethod]
    public void UnsupportedOrInvalidEventKindIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RecoveryNativeMutationPlanner.Start(
                (RecoveryNativeMutationKind)999, Plausible(), Guid.NewGuid()));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                Start(RecoveryNativeMutationKind.PublishPair),
                (RecoveryNativeMutationEvent)999));
    }

    [TestMethod]
    public void ForgedPhaseOrStepCounterCannotBeCancelledIntoAValidSuspension()
    {
        var initial = Start(RecoveryNativeMutationKind.PublishPair);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                initial with { Phase = (RecoveryNativeMutationPhase)999 },
                RecoveryNativeMutationEvent.CancelRequested));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                initial with { Phase = RecoveryNativeMutationPhase.ParentPinned,
                               ObservedSteps = 0 },
                RecoveryNativeMutationEvent.CancelRequested));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                initial with { ObservedSteps = 999 },
                RecoveryNativeMutationEvent.ProcessInterrupted));
    }

    [TestMethod]
    public void CannotReplayCompletedClaimsToProduceAnAuthorization()
    {
        var plan = Apply(Start(RecoveryNativeMutationKind.DeletePair),
            Initial().Concat(new[]
            {
                RecoveryNativeMutationEvent.RevalidateCopyName,
                RecoveryNativeMutationEvent.UnlinkCopyEntry,
                RecoveryNativeMutationEvent.SyncParentDirectory,
                RecoveryNativeMutationEvent.RevalidateManifestName,
                RecoveryNativeMutationEvent.UnlinkManifestEntry,
                RecoveryNativeMutationEvent.SyncParentDirectory
            }).ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeMutationPlanner.Advance(
                plan, RecoveryNativeMutationEvent.UnlinkCopyEntry));
        Assert.IsFalse(plan.CanAuthorizeNativeMutation);
    }
}
