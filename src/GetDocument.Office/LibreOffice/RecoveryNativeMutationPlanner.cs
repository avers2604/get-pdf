namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bx. Pure, deterministic sequence of CLAIMED native operations.
/// Absolutely no OS calls, native delegates, filesystem mutation or access
/// authorization. These event claims can be synthesized by any caller and
/// MUST NOT be used as authority to actually execute an operation.
/// </summary>
internal static class RecoveryNativeMutationPlanner
{
    internal static bool NativeBackendEnabled => false;

    internal static RecoveryNativeMutationPlan Start(
        RecoveryNativeMutationKind kind,
        RecoveryPairIdentityEvidence evidence,
        Guid transactionId)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (transactionId == Guid.Empty)
            throw new ArgumentException(
                "Требуется непустой идентификатор диагностической операции.",
                nameof(transactionId));
        if (RecoveryPairMutationGate.Evaluate(evidence) !=
            RecoveryPairMutationDecision.NeedsNativeBoundOperation)
            throw new InvalidDataException(
                "Нет согласованных диагностических свидетельств идентичности пары.");

        // The evidence contains caller-supplied booleans, NOT a verified
        // privileged kernel capability. Even a "complete" plan is inert.
        return new RecoveryNativeMutationPlan(
            transactionId, kind, RecoveryNativeMutationPhase.Initialized,
            evidence, 0);
    }

    internal static RecoveryNativeMutationPlan Advance(
        RecoveryNativeMutationPlan plan,
        RecoveryNativeMutationEvent action)
    {
        if (!Enum.IsDefined(action))
            throw new InvalidDataException("Неизвестное событие native mutation protocol.");
        if (plan.TransactionId == Guid.Empty ||
            plan.Kind is not (RecoveryNativeMutationKind.PublishPair or
                              RecoveryNativeMutationKind.DeletePair) ||
            RecoveryPairMutationGate.Evaluate(plan.Evidence) !=
                RecoveryPairMutationDecision.NeedsNativeBoundOperation ||
            plan.ObservedSteps != ExpectedStepCount(plan.Kind, plan.Phase))
            throw new InvalidDataException("Диагностический протокол недостоверен.");

        if (plan.Phase is RecoveryNativeMutationPhase.SequenceObserved or
            RecoveryNativeMutationPhase.SuspendedWithoutMutation or
            RecoveryNativeMutationPhase.RequiresManualReview)
            throw new InvalidDataException(
                "Завершённую или приостановленную операцию нельзя продолжать.");

        RecoveryNativeMutationPhase next;
        if (action == RecoveryNativeMutationEvent.ExclusiveLockLost)
            next = RecoveryNativeMutationPhase.RequiresManualReview;
        else if (action is RecoveryNativeMutationEvent.CancelRequested or
                           RecoveryNativeMutationEvent.ProcessInterrupted)
            next = HasClaimedMutation(plan.Phase)
                ? RecoveryNativeMutationPhase.RequiresManualReview
                : RecoveryNativeMutationPhase.SuspendedWithoutMutation;
        else
        {
            next = (plan.Phase, action, plan.Kind) switch
            {
                (RecoveryNativeMutationPhase.Initialized,
                    RecoveryNativeMutationEvent.ObserveParentAnchor, _) =>
                    RecoveryNativeMutationPhase.ParentPinned,
                (RecoveryNativeMutationPhase.ParentPinned,
                    RecoveryNativeMutationEvent.ObserveExclusiveLock, _) =>
                    RecoveryNativeMutationPhase.WriterLockObserved,
                (RecoveryNativeMutationPhase.WriterLockObserved,
                    RecoveryNativeMutationEvent.ObservePairNameAndIdentity, _) =>
                    RecoveryNativeMutationPhase.PairNamesObserved,
                (RecoveryNativeMutationPhase.PairNamesObserved,
                    RecoveryNativeMutationEvent.FlushCopyContent,
                    RecoveryNativeMutationKind.PublishPair) =>
                    RecoveryNativeMutationPhase.CopyBytesDurableClaimed,
                (RecoveryNativeMutationPhase.CopyBytesDurableClaimed,
                    RecoveryNativeMutationEvent.RevalidateCopyName,
                    RecoveryNativeMutationKind.PublishPair) =>
                    RecoveryNativeMutationPhase.CopyNameRevalidated,
                (RecoveryNativeMutationPhase.PairNamesObserved,
                    RecoveryNativeMutationEvent.RevalidateCopyName,
                    RecoveryNativeMutationKind.DeletePair) =>
                    RecoveryNativeMutationPhase.CopyNameRevalidated,
                (RecoveryNativeMutationPhase.CopyNameRevalidated,
                    RecoveryNativeMutationEvent.PublishCopyNoReplace,
                    RecoveryNativeMutationKind.PublishPair) =>
                    RecoveryNativeMutationPhase.CopyNameChanged,
                (RecoveryNativeMutationPhase.CopyNameRevalidated,
                    RecoveryNativeMutationEvent.UnlinkCopyEntry,
                    RecoveryNativeMutationKind.DeletePair) =>
                    RecoveryNativeMutationPhase.CopyNameChanged,
                (RecoveryNativeMutationPhase.CopyNameChanged,
                    RecoveryNativeMutationEvent.SyncParentDirectory, _) =>
                    RecoveryNativeMutationPhase.CopyDirectorySyncedClaimed,
                (RecoveryNativeMutationPhase.CopyDirectorySyncedClaimed,
                    RecoveryNativeMutationEvent.FlushManifestContent,
                    RecoveryNativeMutationKind.PublishPair) =>
                    RecoveryNativeMutationPhase.ManifestBytesDurableClaimed,
                (RecoveryNativeMutationPhase.ManifestBytesDurableClaimed,
                    RecoveryNativeMutationEvent.RevalidateManifestName,
                    RecoveryNativeMutationKind.PublishPair) =>
                    RecoveryNativeMutationPhase.ManifestNameRevalidated,
                (RecoveryNativeMutationPhase.CopyDirectorySyncedClaimed,
                    RecoveryNativeMutationEvent.RevalidateManifestName,
                    RecoveryNativeMutationKind.DeletePair) =>
                    RecoveryNativeMutationPhase.ManifestNameRevalidated,
                (RecoveryNativeMutationPhase.ManifestNameRevalidated,
                    RecoveryNativeMutationEvent.PublishManifestNoReplace,
                    RecoveryNativeMutationKind.PublishPair) =>
                    RecoveryNativeMutationPhase.ManifestNameChanged,
                (RecoveryNativeMutationPhase.ManifestNameRevalidated,
                    RecoveryNativeMutationEvent.UnlinkManifestEntry,
                    RecoveryNativeMutationKind.DeletePair) =>
                    RecoveryNativeMutationPhase.ManifestNameChanged,
                (RecoveryNativeMutationPhase.ManifestNameChanged,
                    RecoveryNativeMutationEvent.SyncParentDirectory, _) =>
                    RecoveryNativeMutationPhase.SequenceObserved,
                _ => throw new InvalidDataException(
                    "Нарушен порядок нативных операций и синхронизации каталога.")
            };
        }

        return plan with
        {
            Phase = next,
            ObservedSteps = checked(plan.ObservedSteps + 1)
        };
    }

    /// <summary>
    /// Reject forged enum states, skipped steps and impossible phase/kind
    /// combinations BEFORE processing crash/cancellation events. A synthetic
    /// progress counter is still not native I/O attestation.
    /// </summary>
    private static int ExpectedStepCount(
        RecoveryNativeMutationKind kind, RecoveryNativeMutationPhase phase) =>
        (kind, phase) switch
        {
            (_, RecoveryNativeMutationPhase.Initialized) => 0,
            (_, RecoveryNativeMutationPhase.ParentPinned) => 1,
            (_, RecoveryNativeMutationPhase.WriterLockObserved) => 2,
            (_, RecoveryNativeMutationPhase.PairNamesObserved) => 3,
            (RecoveryNativeMutationKind.PublishPair,
                RecoveryNativeMutationPhase.CopyBytesDurableClaimed) => 4,
            (RecoveryNativeMutationKind.PublishPair,
                RecoveryNativeMutationPhase.CopyNameRevalidated) => 5,
            (RecoveryNativeMutationKind.DeletePair,
                RecoveryNativeMutationPhase.CopyNameRevalidated) => 4,
            (RecoveryNativeMutationKind.PublishPair,
                RecoveryNativeMutationPhase.CopyNameChanged) => 6,
            (RecoveryNativeMutationKind.DeletePair,
                RecoveryNativeMutationPhase.CopyNameChanged) => 5,
            (RecoveryNativeMutationKind.PublishPair,
                RecoveryNativeMutationPhase.CopyDirectorySyncedClaimed) => 7,
            (RecoveryNativeMutationKind.DeletePair,
                RecoveryNativeMutationPhase.CopyDirectorySyncedClaimed) => 6,
            (RecoveryNativeMutationKind.PublishPair,
                RecoveryNativeMutationPhase.ManifestBytesDurableClaimed) => 8,
            (RecoveryNativeMutationKind.PublishPair,
                RecoveryNativeMutationPhase.ManifestNameRevalidated) => 9,
            (RecoveryNativeMutationKind.DeletePair,
                RecoveryNativeMutationPhase.ManifestNameRevalidated) => 7,
            (RecoveryNativeMutationKind.PublishPair,
                RecoveryNativeMutationPhase.ManifestNameChanged) => 10,
            (RecoveryNativeMutationKind.DeletePair,
                RecoveryNativeMutationPhase.ManifestNameChanged) => 8,
            (RecoveryNativeMutationKind.PublishPair,
                RecoveryNativeMutationPhase.SequenceObserved) => 11,
            (RecoveryNativeMutationKind.DeletePair,
                RecoveryNativeMutationPhase.SequenceObserved) => 9,
            _ => -1
        };

    private static bool HasClaimedMutation(RecoveryNativeMutationPhase phase) =>
        phase is RecoveryNativeMutationPhase.CopyNameChanged or
            RecoveryNativeMutationPhase.CopyDirectorySyncedClaimed or
            RecoveryNativeMutationPhase.ManifestBytesDurableClaimed or
            RecoveryNativeMutationPhase.ManifestNameRevalidated or
            RecoveryNativeMutationPhase.ManifestNameChanged or
            RecoveryNativeMutationPhase.SequenceObserved;
}

internal enum RecoveryNativeMutationKind
{
    PublishPair,
    DeletePair
}

internal enum RecoveryNativeMutationEvent
{
    ObserveParentAnchor,
    ObserveExclusiveLock,
    ObservePairNameAndIdentity,
    FlushCopyContent,
    RevalidateCopyName,
    PublishCopyNoReplace,
    UnlinkCopyEntry,
    SyncParentDirectory,
    FlushManifestContent,
    RevalidateManifestName,
    PublishManifestNoReplace,
    UnlinkManifestEntry,
    CancelRequested,
    ProcessInterrupted,
    ExclusiveLockLost
}

internal enum RecoveryNativeMutationPhase
{
    Initialized,
    ParentPinned,
    WriterLockObserved,
    PairNamesObserved,
    CopyBytesDurableClaimed,
    CopyNameRevalidated,
    CopyNameChanged,
    CopyDirectorySyncedClaimed,
    ManifestBytesDurableClaimed,
    ManifestNameRevalidated,
    ManifestNameChanged,
    SequenceObserved,
    SuspendedWithoutMutation,
    RequiresManualReview
}

/// <summary>
/// A pure in-memory simulation. Not an OS operation receipt or crash journal.
/// It cannot prove that the claimed I/O happened, was atomic, or durable.
/// </summary>
internal readonly record struct RecoveryNativeMutationPlan(
    Guid TransactionId,
    RecoveryNativeMutationKind Kind,
    RecoveryNativeMutationPhase Phase,
    RecoveryPairIdentityEvidence Evidence,
    int ObservedSteps)
{
    internal bool CanAuthorizeNativeMutation => false;
    internal bool CanAuthorizeCleanup => false;
    internal bool ReleaseReady => false;
}
