using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// An authenticated *save request*, not a committed revision. Production
/// storage must independently verify active SSO/ACL, session/replay, the exact
/// editor server DNS/IP, virus scanning, OOXML, GUP-2 CAS and S3 Object Lock.
/// </summary>
public sealed record EuroOfficeSaveCandidate(
    string DocumentKey, int Status, Uri DownloadUrl,
    string SignedPayloadSha256, bool RequiresApprovedRevisionSink = true);

/// <summary>
/// Handles the native DocumentServer callback JSON and separately signed
/// outbox JWT. The older OnlyOfficeCallbackVerifier expects a custom
/// { operation:"callback", payload:{...} } envelope: it is NOT the
/// standard DocumentServer callback contract and is left for legacy tests.
///
/// This class intentionally neither downloads the callback URL nor sends
/// {"error":0}: a valid signature alone is not an authorization to publish.
/// JWT outbox structure and header must be tested against the pinned
/// Euro-Office release before enabling an HTTP callback endpoint.
/// </summary>
public static class EuroOfficeNativeCallback
{
    public static EuroOfficeSaveCandidate? Plan(
        string unsignedCallbackJson,
        string signedOutboxJwt,
        string expectedDocumentKey,
        Uri trustedDocumentServerOrigin,
        byte[] outboxSigningKey)
    {
        if (string.IsNullOrWhiteSpace(unsignedCallbackJson) ||
            unsignedCallbackJson.Length > 65_536 ||
            string.IsNullOrWhiteSpace(expectedDocumentKey) ||
            expectedDocumentKey.Length != 64 ||
            !expectedDocumentKey.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new UnauthorizedAccessException("Некорректные параметры callback.");
        OnlyOfficeUriPolicy.RequireHttpsOrigin(trustedDocumentServerOrigin);

        // Validate cryptographic integrity before parsing any callback fields.
        JsonElement signed = Hs256Jwt.Validate(signedOutboxJwt, outboxSigningKey);
        try
        {
            using JsonDocument callback = JsonDocument.Parse(unsignedCallbackJson);
            JsonElement received = callback.RootElement;
            if (received.ValueKind != JsonValueKind.Object)
                throw new UnauthorizedAccessException("Callback должен быть JSON-объектом.");

            // No permissive fallback to unsigned fields, another claim wrapper
            // or a mismatching key/status/URL.
            string? signedKey = signed.GetProperty("key").GetString();
            string? inputKey = received.GetProperty("key").GetString();
            int signedStatus = signed.GetProperty("status").GetInt32();
            int inputStatus = received.GetProperty("status").GetInt32();
            if (signedKey != expectedDocumentKey || inputKey != signedKey ||
                signedStatus != inputStatus)
                throw new UnauthorizedAccessException("Подпись callback не совпадает с активной редакцией.");

            if (signedStatus is 1 or 4)
                return null;
            if (signedStatus is not (2 or 6))
                throw new InvalidDataException("Euro-Office не подтвердил готовность новой редакции.");

            string? signedUrl = signed.GetProperty("url").GetString();
            string? inputUrl = received.GetProperty("url").GetString();
            if (string.IsNullOrWhiteSpace(signedUrl) ||
                signedUrl.Length > 4096 || inputUrl != signedUrl ||
                !Uri.TryCreate(signedUrl, UriKind.Absolute, out Uri? verifiedUrl))
                throw new UnauthorizedAccessException("Callback содержит неподтверждённый URL.");
            try
            {
                OnlyOfficeUriPolicy.RequireTrustedUrl(
                    verifiedUrl, trustedDocumentServerOrigin);
            }
            catch (ArgumentException exception)
            {
                throw new UnauthorizedAccessException(
                    "Обратную редакцию запрещено загружать с другого сервера.", exception);
            }
            string fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(signedOutboxJwt)))
                .ToLowerInvariant();
            return new EuroOfficeSaveCandidate(
                signedKey!, signedStatus, verifiedUrl, fingerprint);
        }
        catch (Exception exception) when (exception is JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UnauthorizedAccessException("Некорректный подписанный callback.", exception);
        }
    }
}

/// <summary>
/// This is a gate, not a fake corporate sink. All three preconditions must be
/// satisfied by actual infrastructure *and* live ACL checks before writing.
/// </summary>
public static class EuroOfficeCallbackCommitPolicy
{
    public static void RequireReady(bool revisionSinkAvailable,
        bool approvedGup2Cas, bool offlineStoragePinned)
    {
        if (!revisionSinkAvailable || !approvedGup2Cas || !offlineStoragePinned)
            throw new InvalidOperationException(
                "Автосохранение Euro-Office запрещено до подключения GUP-2 CAS, WORM и журнала.");
    }
}
