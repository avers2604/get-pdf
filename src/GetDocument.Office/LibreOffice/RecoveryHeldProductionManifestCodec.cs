using System.Security.Cryptography;
using System.Text.Json;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27cf. Diagnostic parser of the EXISTING production
/// OfficeRecoveryStore.RecoveryManifest wire format:
/// SourcePath, CapturedUtc, Sha256. No new fields or changed production
/// serialization. SourcePath is untrusted TEXT, NEVER opened or normalized.
/// Canonical comparison deliberately rejects ambiguous JSON.
/// </summary>
internal static class RecoveryHeldProductionManifestCodec
{
    internal const int MaxManifestBytes = 2048;
    internal static bool HasTrustedManifestOrigin => false;
    internal static bool CanOpenSourceDocument => false;
    internal static bool CanAuthorizeRestore => false;
    internal static bool CanAuthorizeDelete => false;

    internal static byte[] EncodeCanonical(RecoveryHeldProductionManifestCandidate claim)
    {
        Validate(claim);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(claim);
        if (bytes.Length is < 2 or > MaxManifestBytes)
            throw new InvalidDataException(
                "Манифест аварийной копии превышает 2048 байт.");
        return bytes;
    }

    internal static RecoveryHeldProductionManifestCandidate DecodeCandidate(
        ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 2 or > MaxManifestBytes)
            throw new InvalidDataException(
                "Файл манифеста пуст, повреждён или слишком велик.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions
                {
                    MaxDepth = 4,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Требуется объект манифеста.");

            RecoveryHeldProductionManifestCandidate claim =
                JsonSerializer.Deserialize<RecoveryHeldProductionManifestCandidate>(
                    bytes.Span);
            byte[] canonical = EncodeCanonical(claim);
            if (!CryptographicOperations.FixedTimeEquals(canonical, bytes.Span))
                throw new InvalidDataException(
                    "Манифест имеет неканонический JSON или посторонние поля.");
            return claim;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Неверный JSON существующего манифеста.", ex);
        }
    }

    private static void Validate(RecoveryHeldProductionManifestCandidate claim)
    {
        if (string.IsNullOrWhiteSpace(claim.SourcePath) ||
            claim.SourcePath.Length > 1024 ||
            claim.SourcePath.Any(char.IsControl) ||
            claim.CapturedUtc == default ||
            claim.CapturedUtc.Offset != TimeSpan.Zero ||
            !CanonicalUpperSha256(claim.Sha256))
            throw new InvalidDataException(
                "Не подтверждены поля SourcePath/CapturedUtc/Sha256 манифеста.");
    }

    internal static bool CanonicalUpperSha256(string? sha) =>
        sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal readonly record struct RecoveryHeldProductionManifestCandidate(
    string SourcePath,
    DateTimeOffset CapturedUtc,
    string Sha256);
