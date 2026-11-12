using System.Security.Cryptography;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27ce: BOUNDED LAB-ONLY, NO-WRITE readback of TWO independently
/// caller-held streams. Reads journal -> manifest -> journal -> manifest.
/// This detects some concurrent in-place edits but does not provide atomic
/// snapshots, namespace identity binding or protection from full rollback.
/// The manifest is the CE LAB RECEIPT, not production RecoveryManifest.
/// No automatic restore, rename, unlink, copy hash acquisition, WORM changes
/// or production Store wiring. All permissions are forever false.
/// </summary>
internal static class RecoveryHeldJournalManifestReadback
{
    internal static bool ProductionManifestReaderEnabled => false;
    internal static bool CanAuthorizeCleanup => false;
    internal static bool HasTrustedPersistentAnchor => false;

    internal static async Task<RecoveryHeldPairReadbackResult> ReadOnHeldStreamsAsync(
        Stream heldJournal,
        Stream heldLabManifest,
        Guid expectedTransactionId,
        RecoveryNativeMutationKind expectedKind,
        RecoveryPairIdentityEvidence claimedIdentity,
        string expectedCopySha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heldJournal);
        ArgumentNullException.ThrowIfNull(heldLabManifest);
        if (ReferenceEquals(heldJournal, heldLabManifest))
            throw new ArgumentException(
                "Журнал и квитанция должны быть разными удерживаемыми объектами.");
        if (expectedTransactionId == Guid.Empty ||
            !RecoveryHeldLabManifestCodec.IsCanonicalSha(expectedCopySha256))
            throw new InvalidDataException(
                "Не подтверждены GUID или заявленная SHA-256 копии.");
        if (!heldJournal.CanRead || !heldJournal.CanSeek ||
            !heldLabManifest.CanRead || !heldLabManifest.CanSeek)
            throw new ArgumentException(
                "Оба удерживаемых потока должны быть доступны для bounded read.");
        cancellationToken.ThrowIfCancellationRequested();

        // Two reads can detect some concurrent changes; neither is a
        // protected journal, owner attestation or atomic filesystem snapshot.
        var journalFirst =
            await RecoveryHeldJournalScratchLab.ReadOnHeldStreamAsync(
                heldJournal, expectedTransactionId, expectedKind,
                claimedIdentity, cancellationToken);
        HeldLabManifestReadback manifestFirst =
            await ReadManifestOnceAsync(heldLabManifest, cancellationToken);

        var journalSecond =
            await RecoveryHeldJournalScratchLab.ReadOnHeldStreamAsync(
                heldJournal, expectedTransactionId, expectedKind,
                claimedIdentity, cancellationToken);
        HeldLabManifestReadback manifestSecond =
            await ReadManifestOnceAsync(heldLabManifest, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (journalFirst != journalSecond ||
            manifestFirst != manifestSecond)
            throw new InvalidDataException(
                "Удерживаемый журнал или лабораторный манифест изменился при повторном чтении.");

        RecoveryHeldLabManifestClaim claim = manifestFirst.Claim;
        if (claim.TransactionId != expectedTransactionId ||
            !string.Equals(claim.JournalHeadSha256,
                journalFirst.ClaimedJournalHeadSha256, StringComparison.Ordinal) ||
            !string.Equals(claim.CopySha256, expectedCopySha256,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Квитанция, журнал и заявленный SHA копии не согласуются.");

        return new RecoveryHeldPairReadbackResult(
            RecoveryHeldPairFinding.MatchedTwiceUntrusted,
            expectedTransactionId, journalFirst.ClaimedEvents,
            journalFirst.ContentSha256, manifestFirst.ContentSha256,
            journalFirst.WasReadFromHeldFileStream,
            manifestFirst.WasReadFromHeldFileStream);
    }

    private static async Task<HeldLabManifestReadback> ReadManifestOnceAsync(
        Stream heldManifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long length = heldManifest.Length;
        if (length is < 2 or > RecoveryHeldLabManifestCodec.MaxManifestBytes)
            throw new InvalidDataException(
                "Размер лабораторного манифеста выходит за лимит 1024 байта.");
        RecoveryNativeFileId first = FileIdIfFileStream(heldManifest);
        heldManifest.Position = 0;
        byte[] bytes = new byte[(int)length];
        await heldManifest.ReadExactlyAsync(bytes, cancellationToken);
        if (heldManifest.Length != length)
            throw new InvalidDataException(
                "Лабораторный манифест изменил размер во время чтения.");
        RecoveryHeldLabManifestClaim claim =
            RecoveryHeldLabManifestCodec.DecodeCandidate(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        RecoveryNativeFileId after = FileIdIfFileStream(heldManifest);
        if (first != after)
            throw new InvalidDataException(
                "Изменился File ID удерживаемой квитанции при чтении.");
        heldManifest.Position = 0;
        return new HeldLabManifestReadback(
            claim, Convert.ToHexString(SHA256.HashData(bytes)),
            first.IsComplete);
    }

    private static RecoveryNativeFileId FileIdIfFileStream(Stream stream) =>
        stream is FileStream file
            ? RecoveryNativeHandleIdProbe.ReadFromExistingHandle(
                file.SafeFileHandle)
            : default;
}

internal enum RecoveryHeldPairFinding
{
    MatchedTwiceUntrusted
}

internal readonly record struct RecoveryHeldPairReadbackResult(
    RecoveryHeldPairFinding Finding,
    Guid TransactionId,
    int ClaimedJournalEvents,
    string JournalBytesSha256,
    string ManifestBytesSha256,
    bool JournalReadFromNativeFileStream,
    bool ManifestReadFromNativeFileStream)
{
    internal bool CanRestore => false;
    internal bool CanDelete => false;
    internal bool CanPublish => false;
    internal bool HasTrustedManifest => false;
    internal bool HasIndependentJournalAuthentication => false;
    internal bool HasVerifiedCopyContents => false;
}

internal readonly record struct HeldLabManifestReadback(
    RecoveryHeldLabManifestClaim Claim,
    string ContentSha256,
    bool WasReadFromHeldFileStream);
