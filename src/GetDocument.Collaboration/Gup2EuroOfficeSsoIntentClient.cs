using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Confidential GUP-2 response after a browser-authenticated SSO intent has
/// been redeemed by the already authenticated corporate broker. SaveGrant
/// must never reach desktop Javascript, a browser URL, or logs.
/// </summary>
public sealed record Gup2EuroOfficeSsoRedemption(
    Guid DocumentId, string UserId, int EditVersion, string DocumentKey,
    string Sha256, string Extension, string ServerSaveGrant);

/// <summary>
/// Redeems a separate GUP-2 SSO browser intent over a HMAC-authenticated
/// backend-only channel. This is NOT itself a replacement for Keycloak/AD:
/// caller must verify that employeeId equals its trusted SSO principal.
/// </summary>
public static class Gup2EuroOfficeSsoIntentClient
{
    private const int MaxBytes = 4096;
    private const string Purpose = "getdocument/eurooffice/sso-intent/redeem";

    public static HttpRequestMessage CreateSignedRequest(
        Uri corporateDocumentsOrigin, string browserIntentCode,
        string employeeId, byte[] brokerSigningKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(corporateDocumentsOrigin);
        if (!corporateDocumentsOrigin.IsAbsoluteUri ||
            corporateDocumentsOrigin.Scheme != Uri.UriSchemeHttps ||
            corporateDocumentsOrigin.AbsolutePath != "/documents/" ||
            !string.IsNullOrEmpty(corporateDocumentsOrigin.UserInfo) ||
            !string.IsNullOrEmpty(corporateDocumentsOrigin.Fragment) ||
            !string.IsNullOrEmpty(corporateDocumentsOrigin.Query) ||
            corporateDocumentsOrigin.AbsoluteUri.Contains('%') ||
            !LowerHex(browserIntentCode, 64) ||
            !CanonicalUuid(employeeId) ||
            brokerSigningKey is null || brokerSigningKey.Length < 32)
            throw new ArgumentException("SSO код, пользователь или HTTPS источник не утверждены.");

        string stamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string message = "POST\n" + Purpose + "\n" + browserIntentCode + "\n" +
                         employeeId + "\n" + stamp;
        string signature = Convert.ToHexString(HMACSHA256.HashData(
            brokerSigningKey, Encoding.ASCII.GetBytes(message))).ToLowerInvariant();

        var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(corporateDocumentsOrigin, Purpose + "/"));
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Intent-Code", browserIntentCode);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-User", employeeId);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Timestamp", stamp);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Signature", signature);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    public static Gup2EuroOfficeSsoRedemption ParseRedemption(
        HttpStatusCode httpStatus, string json, string trustedSsoEmployeeId)
    {
        if (httpStatus != HttpStatusCode.OK)
            throw new UnauthorizedAccessException("GUP-2 не подтвердил SSO-сеанс.");
        if (!CanonicalUuid(trustedSsoEmployeeId) ||
            string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes)
            throw new InvalidDataException("Некорректная SSO-аттестация GUP-2.");

        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Недопустимый ответ GUP-2.");
            var keys = root.EnumerateObject().Select(p => p.Name).ToArray();
            var expectedKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "status", "document_id", "user_id", "edit_version",
                "sha256", "extension", "document_key", "save_grant"
            };
            if (keys.Length != expectedKeys.Count ||
                keys.Distinct(StringComparer.Ordinal).Count() != expectedKeys.Count ||
                keys.Any(name => !expectedKeys.Contains(name)))
                throw new InvalidDataException("Неожиданные поля в ответе GUP-2.");

            string? actor = root.GetProperty("user_id").GetString();
            string? doc = root.GetProperty("document_id").GetString();
            string? digest = root.GetProperty("sha256").GetString();
            string? ext = root.GetProperty("extension").GetString();
            string? editorKey = root.GetProperty("document_key").GetString();
            string? grant = root.GetProperty("save_grant").GetString();
            var rev = root.GetProperty("edit_version");
            if (root.GetProperty("status").GetString() != "session_authorized" ||
                actor != trustedSsoEmployeeId || !CanonicalUuid(doc) ||
                !Guid.TryParseExact(doc, "D", out Guid documentId) ||
                rev.ValueKind != JsonValueKind.Number ||
                !rev.TryGetInt32(out int version) || version <= 0 ||
                !LowerHex(digest, 64) || !LowerHex(editorKey, 64) ||
                !LowerHex(grant, 64) || ext is not (".docx" or ".xlsx"))
                throw new InvalidDataException("GUP-2 не подтвердил права сотрудника и версию.");

            string expectedEditorKey = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(doc + "\n" +
                    version.ToString(CultureInfo.InvariantCulture))))
                .ToLowerInvariant();
            if (expectedEditorKey != editorKey)
                throw new InvalidDataException("Неверный ключ корпоративной Office-редакции.");

            return new Gup2EuroOfficeSsoRedemption(
                documentId, actor!, version, editorKey!, digest!, ext!, grant!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Неправильный JSON сервера GUP-2.", ex);
        }
    }

    public static async Task<Gup2EuroOfficeSsoRedemption> RedeemOnceAsync(
        Uri gup2Origin, string browserIntent, string trustedSsoEmployeeId,
        byte[] internalHmacKey, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateSignedRequest(
            gup2Origin, browserIntent, trustedSsoEmployeeId, internalHmacKey, now);
        using var handler = new HttpClientHandler
        {
            UseCookies = false, AllowAutoRedirect = false,
            UseDefaultCredentials = false, PreAuthenticate = false,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength is > MaxBytes)
            throw new UnauthorizedAccessException("GUP-2 отказал в запуске Euro-Office.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[MaxBytes + 1];
        int read = 0;
        while (read < buffer.Length)
        {
            int bytes = await stream.ReadAsync(
                buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (bytes == 0) break;
            read += bytes;
        }
        if (read == buffer.Length)
            throw new InvalidDataException("Ответ авторизации GUP-2 слишком длинный.");
        return ParseRedemption(
            response.StatusCode, Encoding.UTF8.GetString(buffer, 0, read),
            trustedSsoEmployeeId);
    }

    private static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out Guid id) &&
        id != Guid.Empty && id.ToString("D") == value;

    private static bool LowerHex(string? value, int chars) =>
        value is not null && value.Length == chars &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
