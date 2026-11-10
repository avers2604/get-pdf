using System.Security.Cryptography;
using System.Text;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27by: in-memory, BOUNDED, READ-ONLY parser/reconciler for a future
/// crash journal protocol. It neither reads nor writes persistent files.
/// SHA-256 chaining detects accidental corruption and inconsistent excerpts,
/// but does NOT authenticate a journal that an attacker can rewrite entirely.
/// An external expected head only detects truncation relative to that head;
/// it is NOT evidence of independently trusted storage.
/// </summary>
internal static class RecoveryCrashJournalReconciler
{
    internal const int FormatVersion = 1;
    internal const int MaxFrames = 16;
    internal static string GenesisSha256 { get; } = new('0', 64);

    /// <summary>
    /// Construct a synthetic candidate journal frame. NOT an OS receipt.
    /// Canonical ASCII grammar is version|guidN|kindInt|sequence|eventInt|prev.
    /// All numeric formatting is invariant for portable test vectors.
    /// </summary>
    internal static RecoveryCrashJournalFrame CreateSyntheticFrame(
        Guid transactionId,
        RecoveryNativeMutationKind kind,
        int sequence,
        RecoveryNativeMutationEvent action,
        string previousSha256)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("Пустой идентификатор операции.", nameof(transactionId));
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action),
                "Неизвестный тип операции или события.");
        if (sequence is < 1 or > MaxFrames)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        RequireUppercaseSha256(previousSha256);
        string digest = ComputeDigest(transactionId, kind, sequence, action,
            previousSha256);
        return new RecoveryCrashJournalFrame(FormatVersion, transactionId,
            kind, sequence, action, previousSha256, digest);
    }

    /// <summary>
    /// Replays claimed events through the prior BX state machine. Caller
    /// supplies synthetic OS evidence and a separate read-only artifact
    /// observation. All outcomes require manual/native verification.
    /// </summary>
    internal static RecoveryCrashReconciliationReport Analyze(
        IReadOnlyList<RecoveryCrashJournalFrame> frames,
        Guid expectedTransactionId,
        RecoveryNativeMutationKind expectedKind,
        RecoveryPairIdentityEvidence identity,
        RecoveryCrashArtifactSnapshot artifacts,
        string? expectedJournalHeadSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count > MaxFrames)
            throw new InvalidDataException("Превышен лимит 16 записей журнала.");
        if (!Enum.IsDefined(artifacts.Copy) || !Enum.IsDefined(artifacts.Manifest))
            throw new InvalidDataException("Неизвестный тип наблюдения артефактов.");

        // Independently replay claimed sequencing; DO NOT trust recorded
        // 'complete' flags, timestamps, OS identity or unsigned journal data.
        RecoveryNativeMutationPlan plan = RecoveryNativeMutationPlanner.Start(
            expectedKind, identity, expectedTransactionId);
        string previous = GenesisSha256;
        bool mutationClaimed = false;

        for (int i = 0; i < frames.Count; i++)
        {
            RecoveryCrashJournalFrame frame = frames[i];
            if (frame.Version != FormatVersion ||
                frame.TransactionId != expectedTransactionId ||
                frame.Kind != expectedKind ||
                frame.Sequence != i + 1 ||
                !string.Equals(frame.PreviousSha256, previous,
                    StringComparison.Ordinal) ||
                !IsUppercaseSha256(frame.FrameSha256))
                throw new InvalidDataException(
                    "Повреждённая, переставленная или чужая запись журнала.");

            string calculated = ComputeDigest(expectedTransactionId, expectedKind,
                frame.Sequence, frame.Event, frame.PreviousSha256);
            if (!string.Equals(calculated, frame.FrameSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Контрольная цепочка SHA-256 не совпадает с журналом.");

            // Event grammar, including terminal states and duplicate events,
            // is validated independently by the BX planner.
            plan = RecoveryNativeMutationPlanner.Advance(plan, frame.Event);
            mutationClaimed |= frame.Event is
                RecoveryNativeMutationEvent.PublishCopyNoReplace or
                RecoveryNativeMutationEvent.UnlinkCopyEntry or
                RecoveryNativeMutationEvent.PublishManifestNoReplace or
                RecoveryNativeMutationEvent.UnlinkManifestEntry;
            previous = calculated;
        }

        if (expectedJournalHeadSha256 is not null)
        {
            RequireUppercaseSha256(expectedJournalHeadSha256);
            if (!string.Equals(expectedJournalHeadSha256, previous,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Журнал не соответствует предоставленной контрольной вершине.");
        }

        RecoveryCrashReconciliationDisposition disposition =
            Classify(plan, frames.Count, mutationClaimed, artifacts);
        return new RecoveryCrashReconciliationReport(
            disposition, frames.Count, plan.Phase, previous);
    }

    private static RecoveryCrashReconciliationDisposition Classify(
        RecoveryNativeMutationPlan plan, int count,
        bool mutationClaimed, RecoveryCrashArtifactSnapshot snapshot)
    {
        if (snapshot.Copy == RecoveryCrashArtifactClaim.UntrustedLinkOrNonFile ||
            snapshot.Manifest == RecoveryCrashArtifactClaim.UntrustedLinkOrNonFile)
            return RecoveryCrashReconciliationDisposition.UntrustedArtifact;

        if (snapshot.Copy is RecoveryCrashArtifactClaim.NotObserved or
            RecoveryCrashArtifactClaim.MetadataUnavailable ||
            snapshot.Manifest is RecoveryCrashArtifactClaim.NotObserved or
                RecoveryCrashArtifactClaim.MetadataUnavailable)
            return RecoveryCrashReconciliationDisposition.MetadataUnavailable;

        bool copyPresent = snapshot.Copy == RecoveryCrashArtifactClaim.RegularFileObserved;
        bool manifestPresent =
            snapshot.Manifest == RecoveryCrashArtifactClaim.RegularFileObserved;
        bool completed = plan.Phase == RecoveryNativeMutationPhase.SequenceObserved;

        if (completed &&
            (plan.Kind == RecoveryNativeMutationKind.PublishPair &&
                (!copyPresent || !manifestPresent) ||
             plan.Kind == RecoveryNativeMutationKind.DeletePair &&
                (copyPresent || manifestPresent)))
            return RecoveryCrashReconciliationDisposition.JournalConflictsWithArtifactObservation;

        if (copyPresent && !manifestPresent)
            return RecoveryCrashReconciliationDisposition.CopyWithoutManifest;

        if (!copyPresent && manifestPresent)
            return RecoveryCrashReconciliationDisposition.ManifestWithoutCopy;

        if (count == 0)
            return RecoveryCrashReconciliationDisposition.NoJournalEvidence;

        if (completed)
            return RecoveryCrashReconciliationDisposition.ClaimedCompleteUnverified;

        return mutationClaimed
            ? RecoveryCrashReconciliationDisposition.PartialMutationClaim
            : RecoveryCrashReconciliationDisposition.InterruptedBeforeMutationClaim;
    }

    private static string ComputeDigest(
        Guid id, RecoveryNativeMutationKind kind, int sequence,
        RecoveryNativeMutationEvent action, string previousSha256)
    {
        RequireUppercaseSha256(previousSha256);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(action))
            throw new InvalidDataException("Неизвестное событие в контрольной цепочке.");
        string canonical = FormattableString.Invariant(
            $"{FormatVersion}|{id:N}|{(int)kind}|{sequence}|{(int)action}|{previousSha256}");
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(canonical)));
    }

    private static bool IsUppercaseSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void RequireUppercaseSha256(string? digest)
    {
        if (!IsUppercaseSha256(digest))
            throw new InvalidDataException("Требуется каноническая SHA-256 запись.");
    }
}

internal readonly record struct RecoveryCrashJournalFrame(
    int Version,
    Guid TransactionId,
    RecoveryNativeMutationKind Kind,
    int Sequence,
    RecoveryNativeMutationEvent Event,
    string PreviousSha256,
    string FrameSha256);

internal enum RecoveryCrashArtifactClaim
{
    NotObserved,
    Missing,
    RegularFileObserved,
    UntrustedLinkOrNonFile,
    MetadataUnavailable
}

internal readonly record struct RecoveryCrashArtifactSnapshot(
    RecoveryCrashArtifactClaim Copy,
    RecoveryCrashArtifactClaim Manifest);

internal enum RecoveryCrashReconciliationDisposition
{
    NoJournalEvidence,
    InterruptedBeforeMutationClaim,
    PartialMutationClaim,
    ClaimedCompleteUnverified,
    CopyWithoutManifest,
    ManifestWithoutCopy,
    JournalConflictsWithArtifactObservation,
    UntrustedArtifact,
    MetadataUnavailable
}

/// <summary>
/// An observational report, not evidence of journal authenticity or of
/// verified file contents. Never gives restore/delete/publication rights.
/// </summary>
internal readonly record struct RecoveryCrashReconciliationReport(
    RecoveryCrashReconciliationDisposition Disposition,
    int ReplayedFrames,
    RecoveryNativeMutationPhase ClaimedPhase,
    string ClaimedJournalHeadSha256)
{
    internal bool HasIndependentJournalAuthentication => false;
    internal bool CanAuthorizeRestoration => false;
    internal bool CanAuthorizeCleanup => false;
    internal bool CanAuthorizePublication => false;
}
