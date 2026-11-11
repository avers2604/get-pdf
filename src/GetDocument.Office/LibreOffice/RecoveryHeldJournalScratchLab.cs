using System.Security.Cryptography;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27ca / blocks 1–2. Diagnostic laboratory that writes a bounded
/// journal to an EXTERNALLY OWNED, ALREADY OPEN, EMPTY seekable Stream.
/// Never creates paths, chooses a destination, opens a user document, renames,
/// unlinks, truncates after failure, or returns a native trust capability.
/// Even FileStream.Flush(true) does NOT prove directory-entry durability.
/// DO NOT wire into production OfficeRecoveryStore.
/// </summary>
internal static class RecoveryHeldJournalScratchLab
{
    internal static bool ProductionWriterEnabled => false;
    internal static bool CanCleanupByPath => false;

    internal static async Task<RecoveryHeldJournalScratchReceipt> StageOnHeldStreamAsync(
        Stream ownedEmptyScratch, ReadOnlyMemory<byte> encodedJournal,
        Guid expectedTransactionId, RecoveryNativeMutationKind expectedKind,
        RecoveryPairIdentityEvidence claimedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownedEmptyScratch);
        if (ReferenceEquals(ownedEmptyScratch, Stream.Null) ||
            !ownedEmptyScratch.CanRead || !ownedEmptyScratch.CanWrite ||
            !ownedEmptyScratch.CanSeek || ownedEmptyScratch.Length != 0 ||
            ownedEmptyScratch.Position != 0)
            throw new ArgumentException(
                "Требуется отдельный новый пустой seekable поток, принадлежащий вызывающему коду.",
                nameof(ownedEmptyScratch));

        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = encodedJournal.ToArray();
        RecoveryCrashJournalFrame[] frames =
            RecoveryCrashJournalCodec.DecodeCandidate(bytes);
        if (frames.Length == 0)
            throw new InvalidDataException("Пустой диагностический журнал нельзя записывать.");

        // Entire synthetic chain must be valid BEFORE writing first byte.
        RecoveryCrashReconciliationReport parsed =
            RecoveryCrashJournalReconciler.Analyze(frames, expectedTransactionId,
                expectedKind, claimedIdentity,
                new RecoveryCrashArtifactSnapshot(
                    RecoveryCrashArtifactClaim.NotObserved,
                    RecoveryCrashArtifactClaim.NotObserved));
        cancellationToken.ThrowIfCancellationRequested();

        RecoveryNativeFileId identityAtStart =
            ObserveFileIdIfExistingFileStream(ownedEmptyScratch);
        byte[] expectedSha = SHA256.HashData(bytes);

        // Intentionally NO catch/rollback/delete/truncate on partial I/O or
        // cancellation. Such bytes are untrusted orphan artifacts owned by
        // the external scratch-file provider and need manual reconciliation.
        await ownedEmptyScratch.WriteAsync(encodedJournal, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ownedEmptyScratch.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        bool contentFlushAttempted = false;
        if (ownedEmptyScratch is FileStream openedFile)
        {
            // Actually attempts OS file-content flush on a CALLER-OPENED fd.
            // No dirfd fsync, Windows NTFS validation or crash durability.
            openedFile.Flush(flushToDisk: true);
            contentFlushAttempted = true;
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Re-read from precisely the SAME held Stream object, not a name.
        // A distinct reader and rename-resistant entry binding are future work.
        if (ownedEmptyScratch.Length != bytes.Length)
            throw new InvalidDataException(
                "Размер подготовленного журнала изменился после записи.");
        ownedEmptyScratch.Position = 0;
        byte[] observed = new byte[bytes.Length];
        await ownedEmptyScratch.ReadExactlyAsync(observed, cancellationToken);
        if (ownedEmptyScratch.Length != bytes.Length ||
            !CryptographicOperations.FixedTimeEquals(observed, expectedSha.Length == 0
                ? Array.Empty<byte>() : bytes))
            throw new InvalidDataException("Повторно прочитанные байты журнала отличаются.");
        cancellationToken.ThrowIfCancellationRequested();

        RecoveryNativeFileId identityAtFinish =
            ObserveFileIdIfExistingFileStream(ownedEmptyScratch);
        if (identityAtStart != identityAtFinish)
            throw new InvalidDataException(
                "Идентификатор удерживаемого файла изменился во время диагностической записи.");
        ownedEmptyScratch.Position = 0;

        return new RecoveryHeldJournalScratchReceipt(
            bytes.Length, Convert.ToHexString(expectedSha),
            parsed.ClaimedJournalHeadSha256,
            contentFlushAttempted,
            identityAtStart.IsComplete);
    }

    /// <summary>
    /// Read a caller-reopened, held stream (possibly the same scratch stream).
    /// No file name, folder traversal, deletion or restoring of documents.
    /// Partial and invalid envelopes fail closed.
    /// </summary>
    internal static async Task<RecoveryHeldJournalReadbackReport> ReadOnHeldStreamAsync(
        Stream callerOpenedJournal, Guid expectedTransactionId,
        RecoveryNativeMutationKind expectedKind,
        RecoveryPairIdentityEvidence claimedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callerOpenedJournal);
        if (ReferenceEquals(callerOpenedJournal, Stream.Null) ||
            !callerOpenedJournal.CanRead || !callerOpenedJournal.CanSeek)
            throw new ArgumentException(
                "Повторное чтение требует удерживаемый seekable read stream.",
                nameof(callerOpenedJournal));
        cancellationToken.ThrowIfCancellationRequested();

        long size = callerOpenedJournal.Length;
        if (size < 1 || size > RecoveryCrashJournalCodec.MaxEnvelopeBytes)
            throw new InvalidDataException("Размер журнала за пределами 1–8192 байт.");
        RecoveryNativeFileId identityAtOpen =
            ObserveFileIdIfExistingFileStream(callerOpenedJournal);

        // Begin at file offset zero, bounded memory and no lazy untrusted
        // enumeration. Stream is caller-owned and remains open on failure.
        callerOpenedJournal.Position = 0;
        byte[] bytes = new byte[(int)size];
        await callerOpenedJournal.ReadExactlyAsync(bytes, cancellationToken);
        if (callerOpenedJournal.Length != size)
            throw new InvalidDataException("Размер файла изменился во время повторного чтения.");

        RecoveryCrashJournalFrame[] frames =
            RecoveryCrashJournalCodec.DecodeCandidate(bytes);
        if (frames.Length == 0)
            throw new InvalidDataException("Пустой журнал не является квитанцией.");
        var report = RecoveryCrashJournalReconciler.Analyze(
            frames, expectedTransactionId, expectedKind, claimedIdentity,
            new RecoveryCrashArtifactSnapshot(
                RecoveryCrashArtifactClaim.NotObserved,
                RecoveryCrashArtifactClaim.NotObserved));
        cancellationToken.ThrowIfCancellationRequested();
        RecoveryNativeFileId identityAtFinish =
            ObserveFileIdIfExistingFileStream(callerOpenedJournal);
        if (identityAtOpen != identityAtFinish)
            throw new InvalidDataException("Открытый журнал изменился при чтении.");
        callerOpenedJournal.Position = 0;

        return new RecoveryHeldJournalReadbackReport(
            frames.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            report.ClaimedJournalHeadSha256,
            identityAtOpen.IsComplete);
    }

    private static RecoveryNativeFileId ObserveFileIdIfExistingFileStream(
        Stream candidate)
    {
        if (candidate is not FileStream fileStream) return default;
        return RecoveryNativeHandleIdProbe.ReadFromExistingHandle(
            fileStream.SafeFileHandle);
    }
}

internal readonly record struct RecoveryHeldJournalScratchReceipt(
    int ByteCount,
    string ContentSha256,
    string ClaimedJournalHeadSha256,
    bool FileContentFlushAttempted,
    bool WasWrittenOnHeldFileStream)
{
    internal bool DirectoryEntryDurabilityVerified => false;
    internal bool ContentDurabilityVerified => false;
    internal bool CanAuthorizeRecovery => false;
    internal bool CanAuthorizeCleanup => false;
}

internal readonly record struct RecoveryHeldJournalReadbackReport(
    int ClaimedEvents,
    string ContentSha256,
    string ClaimedJournalHeadSha256,
    bool WasReadFromHeldFileStream)
{
    internal bool CanRestore => false;
    internal bool CanDelete => false;
    internal bool CanPublish => false;
    internal bool HasTrustedPersistence => false;
}
