using System.Security.Cryptography;
using System.Text.Json;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27ce laboratory-only counterpart of a crash journal receipt.
/// NOT OfficeRecoveryStore.RecoveryManifest (which contains SourcePath,
/// CapturedUtc, Sha256). Never read this shape as a production manifest.
/// Candidate values and JSON bytes have NO independent authenticity.
/// </summary>
internal static class RecoveryHeldLabManifestCodec
{
    internal const int Version = 1;
    internal const int MaxManifestBytes = 1024;
    internal static bool IsProductionManifestFormat => false;
    internal static bool HasIndependentAuthentication => false;

    internal static byte[] EncodeCandidate(RecoveryHeldLabManifestClaim claim)
    {
        Validate(claim);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(claim);
        if (bytes.Length is < 2 or > MaxManifestBytes)
            throw new InvalidDataException(
                "Лабораторная квитанция не укладывается в 1024 байта.");
        return bytes;
    }

    internal static RecoveryHeldLabManifestClaim DecodeCandidate(
        ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length is < 2 or > MaxManifestBytes)
            throw new InvalidDataException(
                "Лабораторный манифест повреждён или превышает лимит.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(encoded,
                new JsonDocumentOptions
                {
                    MaxDepth = 4,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ожидался объект квитанции.");

            RecoveryHeldLabManifestClaim claim =
                JsonSerializer.Deserialize<RecoveryHeldLabManifestClaim>(encoded.Span);
            byte[] canonical = EncodeCandidate(claim);
            if (!CryptographicOperations.FixedTimeEquals(
                    canonical, encoded.Span))
                throw new InvalidDataException(
                    "Лабораторная квитанция неканонична или содержит дополнительные поля.");
            return claim;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("JSON квитанции не соответствует формату.", ex);
        }
    }

    private static void Validate(RecoveryHeldLabManifestClaim claim)
    {
        if (claim.Version != Version || claim.TransactionId == Guid.Empty ||
            !IsCanonicalSha(claim.JournalHeadSha256) ||
            !IsCanonicalSha(claim.CopySha256))
            throw new InvalidDataException(
                "Неверная версия, GUID или checksum лабораторной квитанции.");
    }

    internal static bool IsCanonicalSha(string? sha) =>
        sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal readonly record struct RecoveryHeldLabManifestClaim(
    int Version,
    Guid TransactionId,
    string JournalHeadSha256,
    string CopySha256);
