using System.Security.Cryptography;
using System.Text;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bz / BLOCK 2: synthetic HMAC-bound anchor candidate.
/// The HMAC only demonstrates consistency with the CALLER-SUPPLIED key.
/// The key is NOT sourced from an independently protected OS service.
/// No disk storage, key persistence, trusted generation counter, or mutation.
/// </summary>
internal static class RecoveryProtectedAnchorProtocol
{
    internal const int FormatVersion = 1;
    internal static bool NativeTrustStoreConnected => false;

    internal static RecoveryProtectedAnchorCandidate CreateSynthetic(
        Guid transactionId, ulong generation,
        string journalHeadSha256, string previousAnchorMacSha256,
        ReadOnlySpan<byte> suppliedKey)
    {
        ValidateInput(transactionId, generation, journalHeadSha256,
            previousAnchorMacSha256, suppliedKey);
        string mac = ComputeMac(transactionId, generation,
            journalHeadSha256, previousAnchorMacSha256, suppliedKey);
        return new RecoveryProtectedAnchorCandidate(FormatVersion,
            transactionId, generation, journalHeadSha256,
            previousAnchorMacSha256, mac);
    }

    internal static RecoveryAnchorAssessment AssessCandidate(
        RecoveryProtectedAnchorCandidate candidate,
        ReadOnlySpan<byte> suppliedKey,
        Guid expectedTransactionId,
        ulong expectedGeneration,
        string expectedJournalHeadSha256,
        string expectedPreviousAnchorMacSha256)
    {
        ValidateInput(expectedTransactionId, expectedGeneration,
            expectedJournalHeadSha256, expectedPreviousAnchorMacSha256, suppliedKey);
        if (candidate.TransactionId == Guid.Empty || candidate.Generation == 0 ||
            !IsCanonicalSha(candidate.JournalHeadSha256) ||
            !IsCanonicalSha(candidate.PreviousAnchorMacSha256) ||
            !IsCanonicalSha(candidate.MacSha256))
            throw new InvalidDataException("Синтетическая контрольная вершина повреждена.");

        if (candidate.Version != FormatVersion)
            return new RecoveryAnchorAssessment(RecoveryAnchorFinding.VersionMismatch, false);
        if (candidate.TransactionId != expectedTransactionId)
            return new RecoveryAnchorAssessment(RecoveryAnchorFinding.TransactionMismatch, false);
        if (candidate.Generation != expectedGeneration)
            return new RecoveryAnchorAssessment(RecoveryAnchorFinding.GenerationMismatch, false);
        if (!string.Equals(candidate.JournalHeadSha256, expectedJournalHeadSha256,
                StringComparison.Ordinal))
            return new RecoveryAnchorAssessment(RecoveryAnchorFinding.JournalHeadMismatch, false);
        if (!string.Equals(candidate.PreviousAnchorMacSha256,
                expectedPreviousAnchorMacSha256, StringComparison.Ordinal))
            return new RecoveryAnchorAssessment(RecoveryAnchorFinding.PreviousTagMismatch, false);

        string expectedMac = ComputeMac(candidate.TransactionId,
            candidate.Generation, candidate.JournalHeadSha256,
            candidate.PreviousAnchorMacSha256, suppliedKey);
        byte[] expected = Convert.FromHexString(expectedMac);
        byte[] observed = Convert.FromHexString(candidate.MacSha256);
        bool same = CryptographicOperations.FixedTimeEquals(expected, observed);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(observed);
        return new RecoveryAnchorAssessment(
            same ? RecoveryAnchorFinding.MatchedButUntrusted
                 : RecoveryAnchorFinding.TagMismatch,
            same);
    }

    private static string ComputeMac(Guid transactionId, ulong generation,
        string journalHead, string previousTag, ReadOnlySpan<byte> suppliedKey)
    {
        // Fixed-width, canonical, unambiguous payload; key not serialized.
        string canonical = FormattableString.Invariant(
            $"GET-BZ1|{transactionId:N}|{generation}|{journalHead}|{previousTag}");
        byte[] message = Encoding.ASCII.GetBytes(canonical);
        byte[] mac = HMACSHA256.HashData(suppliedKey, message);
        try
        {
            return Convert.ToHexString(mac);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    private static void ValidateInput(Guid transactionId, ulong generation,
        string journalHeadSha256, string previousAnchorMacSha256,
        ReadOnlySpan<byte> suppliedKey)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("Требуется идентификатор операции.",
                nameof(transactionId));
        if (suppliedKey.Length is < 32 or > 64)
            throw new ArgumentOutOfRangeException(nameof(suppliedKey),
                "Ключ синтетической проверки должен содержать 32–64 байта.");
        if (generation == 0 || !IsCanonicalSha(journalHeadSha256) ||
            !IsCanonicalSha(previousAnchorMacSha256))
            throw new InvalidDataException("Неканонические поля или поколение вершины.");
    }

    private static bool IsCanonicalSha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal enum RecoveryAnchorFinding
{
    VersionMismatch,
    TransactionMismatch,
    GenerationMismatch,
    JournalHeadMismatch,
    PreviousTagMismatch,
    TagMismatch,
    MatchedButUntrusted
}

internal readonly record struct RecoveryProtectedAnchorCandidate(
    int Version,
    Guid TransactionId,
    ulong Generation,
    string JournalHeadSha256,
    string PreviousAnchorMacSha256,
    string MacSha256);

internal readonly record struct RecoveryAnchorAssessment(
    RecoveryAnchorFinding Finding,
    bool TagMatchedWithSuppliedKey)
{
    internal bool HasIndependentlyTrustedKey => false;
    internal bool CanAuthorizeRecovery => false;
    internal bool CanAuthorizeDeletion => false;
    internal bool CanAuthorizePublication => false;
}
