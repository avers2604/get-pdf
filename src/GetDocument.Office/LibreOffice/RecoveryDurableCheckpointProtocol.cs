namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bz / BLOCK 1: PURE protocol of claimed durability steps.
/// This is not an OS journal or persistent storage implementation.
/// Caller-injected events do not prove fsync, lock ownership or atomicity.
/// </summary>
internal static class RecoveryDurableCheckpointProtocol
{
    internal static bool NativeStoreEnabled => false;

    internal static RecoveryDurableCheckpointPlan Start(
        Guid transactionId, ulong priorGeneration, ulong nextGeneration,
        string journalHeadSha256, int frameCount)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("Не задан идентификатор транзакции.",
                nameof(transactionId));
        if (priorGeneration == ulong.MaxValue ||
            nextGeneration != priorGeneration + 1 ||
            frameCount is < 1 or > RecoveryCrashJournalReconciler.MaxFrames ||
            !IsCanonicalSha256(journalHeadSha256))
            throw new InvalidDataException(
                "Не подтверждён новый счётчик поколения или SHA-256 журнала.");

        return new RecoveryDurableCheckpointPlan(transactionId,
            priorGeneration, nextGeneration, journalHeadSha256,
            frameCount, RecoveryDurableCheckpointPhase.Initialized, 0);
    }

    internal static RecoveryDurableCheckpointPlan Advance(
        RecoveryDurableCheckpointPlan plan, RecoveryDurableCheckpointEvent action)
    {
        if (!Enum.IsDefined(action) || plan.TransactionId == Guid.Empty ||
            plan.PriorGeneration == ulong.MaxValue ||
            plan.NextGeneration != plan.PriorGeneration + 1 ||
            plan.FrameCount is < 1 or > RecoveryCrashJournalReconciler.MaxFrames ||
            !IsCanonicalSha256(plan.JournalHeadSha256) ||
            plan.ObservedSteps != ExpectedStepCount(plan.Phase))
            throw new InvalidDataException("Неизвестное или поддельное состояние протокола.");

        if (plan.Phase is RecoveryDurableCheckpointPhase.ReadbackClaimed or
            RecoveryDurableCheckpointPhase.SuspendedWithoutWriteClaim or
            RecoveryDurableCheckpointPhase.RequiresManualReview)
            throw new InvalidDataException("Завершённый план нельзя продолжать.");

        RecoveryDurableCheckpointPhase next;
        if (action is RecoveryDurableCheckpointEvent.LockLost or
            RecoveryDurableCheckpointEvent.MediaError)
            next = RecoveryDurableCheckpointPhase.RequiresManualReview;
        else if (action is RecoveryDurableCheckpointEvent.CancelRequested or
            RecoveryDurableCheckpointEvent.ProcessInterrupted)
            next = plan.Phase == RecoveryDurableCheckpointPhase.Initialized
                ? RecoveryDurableCheckpointPhase.SuspendedWithoutWriteClaim
                : RecoveryDurableCheckpointPhase.RequiresManualReview;
        else
            next = (plan.Phase, action) switch
            {
                (RecoveryDurableCheckpointPhase.Initialized,
                    RecoveryDurableCheckpointEvent.JournalFileFlushClaimed) =>
                    RecoveryDurableCheckpointPhase.JournalFileSyncedClaimed,
                (RecoveryDurableCheckpointPhase.JournalFileSyncedClaimed,
                    RecoveryDurableCheckpointEvent.JournalDirectorySyncClaimed) =>
                    RecoveryDurableCheckpointPhase.JournalDirectorySyncedClaimed,
                (RecoveryDurableCheckpointPhase.JournalDirectorySyncedClaimed,
                    RecoveryDurableCheckpointEvent.AnchorFileFlushClaimed) =>
                    RecoveryDurableCheckpointPhase.AnchorFileSyncedClaimed,
                (RecoveryDurableCheckpointPhase.AnchorFileSyncedClaimed,
                    RecoveryDurableCheckpointEvent.AnchorDirectorySyncClaimed) =>
                    RecoveryDurableCheckpointPhase.AnchorDirectorySyncedClaimed,
                (RecoveryDurableCheckpointPhase.AnchorDirectorySyncedClaimed,
                    RecoveryDurableCheckpointEvent.RestartReadbackClaimed) =>
                    RecoveryDurableCheckpointPhase.ReadbackClaimed,
                _ => throw new InvalidDataException(
                    "Неверный порядок file/dir flush или повторная попытка.")
            };

        return plan with
        {
            Phase = next,
            ObservedSteps = checked(plan.ObservedSteps + 1)
        };
    }

    private static int ExpectedStepCount(RecoveryDurableCheckpointPhase phase) =>
        phase switch
        {
            RecoveryDurableCheckpointPhase.Initialized => 0,
            RecoveryDurableCheckpointPhase.JournalFileSyncedClaimed => 1,
            RecoveryDurableCheckpointPhase.JournalDirectorySyncedClaimed => 2,
            RecoveryDurableCheckpointPhase.AnchorFileSyncedClaimed => 3,
            RecoveryDurableCheckpointPhase.AnchorDirectorySyncedClaimed => 4,
            _ => -1
        };

    private static bool IsCanonicalSha256(string? sha) =>
        sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal enum RecoveryDurableCheckpointEvent
{
    JournalFileFlushClaimed,
    JournalDirectorySyncClaimed,
    AnchorFileFlushClaimed,
    AnchorDirectorySyncClaimed,
    RestartReadbackClaimed,
    CancelRequested,
    ProcessInterrupted,
    LockLost,
    MediaError
}

internal enum RecoveryDurableCheckpointPhase
{
    Initialized,
    JournalFileSyncedClaimed,
    JournalDirectorySyncedClaimed,
    AnchorFileSyncedClaimed,
    AnchorDirectorySyncedClaimed,
    ReadbackClaimed,
    SuspendedWithoutWriteClaim,
    RequiresManualReview
}

internal readonly record struct RecoveryDurableCheckpointPlan(
    Guid TransactionId,
    ulong PriorGeneration,
    ulong NextGeneration,
    string JournalHeadSha256,
    int FrameCount,
    RecoveryDurableCheckpointPhase Phase,
    int ObservedSteps)
{
    internal bool HasIndependentlyVerifiedDurability => false;
    internal bool CanAuthorizeMutation => false;
    internal bool CanAuthorizeCleanup => false;
    internal bool ReleaseReady => false;
}
