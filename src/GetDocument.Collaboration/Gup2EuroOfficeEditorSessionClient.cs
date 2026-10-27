using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Sensitive, short-lived internal session receipt. No ToString() record
/// output or logging: the save grant is usable only by the trusted broker.
/// The save grant must NEVER be serialized into Docs API JSON or WebView.
/// </summary>
public sealed class Gup2EuroOfficeEditorSessionReceipt
{
    public required Guid DocumentId { get; init; }
    public required string EmployeeId { get; init; }
    public required int ActorAuthVersion { get; init; }
    public required int EditVersion { get; init; }
    public required string DocumentKey { get; init; }
    public required string OriginalSha256 { get; init; }
    public required string WorkingSha256 { get; init; }
    public required string Extension { get; init; }
    public required string SaveGrant { get; init; }

    public override string ToString() => nameof(Gup2EuroOfficeEditorSessionReceipt);
}

/// <summary>
/// Calls the independently authenticated GUP-2 service endpoint. This is
/// NOT a user login provider and does not implement ITrustedEuroOfficeLaunchStore.
/// The caller MUST already authenticate the corporate SSO principal and keep
/// this service secret on the server (never Desktop or the browser).
/// </summary>
public static class Gup2EuroOfficeEditorSessionClient
{
    private const string Purpose = "getdocument/eurooffice/session/redeem";
    private const int MaxReceiptBytes = 4096;

    public static HttpRequestMessage CreateSignedRequest(
        Uri trustedDocumentsOrigin, string oneTimeLaunchId, string employeeId,
        byte[] serviceHmacKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trustedDocumentsOrigin);
        if (!trustedDocumentsOrigin.IsAbsoluteUri ||
            trustedDocumentsOrigin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(trustedDocumentsOrigin.UserInfo) ||
            !string.IsNullOrEmpty(trustedDocumentsOrigin.Query) ||
            !string.IsNullOrEmpty(trustedDocumentsOrigin.Fragment) ||
            trustedDocumentsOrigin.AbsolutePath != "/documents/" ||
            trustedDocumentsOrigin.AbsoluteUri.Contains('%'))
            throw new ArgumentException("Необходим закреплённый HTTPS-адрес GUP-2.");

        if (!EuroOfficeLaunchId.IsValid(oneTimeLaunchId) ||
            !CanonicalUuid(employeeId) ||
            serviceHmacKey is null || serviceHmacKey.Length < 32)
            throw new ArgumentException("Неверный служебный запуск, сотрудник или ключ.");

        string stamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string canonical = "POST\n" + Purpose + "\n" + oneTimeLaunchId +
                           "\n" + employeeId + "\n" + stamp;
        string signature = Convert.ToHexString(HMACSHA256.HashData(
            serviceHmacKey, Encoding.ASCII.GetBytes(canonical))).ToLowerInvariant();

        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(trustedDocumentsOrigin, Purpose + "/"));
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation(
            "X-Get-Eurooffice-Session-Code", oneTimeLaunchId);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-User", employeeId);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Timestamp", stamp);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Signature", signature);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    public static string CalculateDocumentKey(string canonicalDocumentId, int editVersion)
    {
        if (!CanonicalUuid(canonicalDocumentId) || editVersion < 0)
            throw new ArgumentException("Недопустимая идентичность рабочей редакции.");
        string source = canonicalDocumentId + "\n" +
                        editVersion.ToString(CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    public static Gup2EuroOfficeEditorSessionReceipt ParseReceipt(
        string json, ClaimsPrincipal authenticatedCorporateSsoUser)
    {
        string? authenticatedId =
            authenticatedCorporateSsoUser?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (authenticatedCorporateSsoUser?.Identity?.IsAuthenticated != true ||
            !CanonicalUuid(authenticatedId))
            throw new UnauthorizedAccessException(
                "Корпоративная SSO-сессия не содержит GUP-2 UUID.");

        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxReceiptBytes)
            throw new InvalidDataException("Некорректный ответ SSO запуска.");
        try
        {
            using var parsed = JsonDocument.Parse(json);
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("SSO ответ должен быть объектом.");

            string[] required =
            [
                "status", "document_id", "user_id", "auth_version",
                "edit_version", "document_key", "original_sha256",
                "sha256", "extension", "save_grant"
            ];
            string[] actual = root.EnumerateObject().Select(x => x.Name).ToArray();
            if (actual.Length != required.Length ||
                actual.Distinct(StringComparer.Ordinal).Count() != required.Length ||
                actual.Any(x => !required.Contains(x, StringComparer.Ordinal)) ||
                root.GetProperty("status").GetString() != "session_bound")
                throw new InvalidDataException("Недопустимые полномочия в ответе GUP-2.");

            string? employee = root.GetProperty("user_id").GetString();
            if (!string.Equals(employee, authenticatedId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("GUP-2 и SSO сотрудник не совпадают.");

            string? document = root.GetProperty("document_id").GetString();
            string? documentKey = root.GetProperty("document_key").GetString();
            string? originalSha = root.GetProperty("original_sha256").GetString();
            string? workingSha = root.GetProperty("sha256").GetString();
            string? extension = root.GetProperty("extension").GetString();
            string? saveGrant = root.GetProperty("save_grant").GetString();

            JsonElement edit = root.GetProperty("edit_version");
            JsonElement auth = root.GetProperty("auth_version");
            if (!CanonicalUuid(document) ||
                !Guid.TryParseExact(document, "D", out Guid documentId) ||
                edit.ValueKind != JsonValueKind.Number ||
                !edit.TryGetInt32(out int editVersion) || editVersion < 0 ||
                auth.ValueKind != JsonValueKind.Number ||
                !auth.TryGetInt32(out int authVersion) || authVersion < 0 ||
                !LowerHex(documentKey, 64) ||
                documentKey != CalculateDocumentKey(document!, editVersion) ||
                !LowerHex(originalSha, 64) || !LowerHex(workingSha, 64) ||
                !LowerHex(saveGrant, 64) ||
                extension is not (".docx" or ".xlsx"))
                throw new InvalidDataException("SSO метаданные GUP-2 не подтверждены.");

            return new Gup2EuroOfficeEditorSessionReceipt
            {
                DocumentId = documentId,
                EmployeeId = employee!,
                ActorAuthVersion = authVersion,
                EditVersion = editVersion,
                DocumentKey = documentKey!,
                OriginalSha256 = originalSha!,
                WorkingSha256 = workingSha!,
                Extension = extension!,
                SaveGrant = saveGrant!
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Некорректный JSON ответа SSO GUP-2.", ex);
        }
    }

    public static async Task<Gup2EuroOfficeEditorSessionReceipt> RedeemAsync(
        Uri trustedDocumentsOrigin, string oneTimeLaunchId,
        ClaimsPrincipal authenticatedCorporateSsoUser,
        byte[] serviceHmacKey, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authenticatedCorporateSsoUser?.Identity?.IsAuthenticated != true ||
            !CanonicalUuid(authenticatedCorporateSsoUser.FindFirst(
                ClaimTypes.NameIdentifier)?.Value))
            throw new UnauthorizedAccessException("Требуется корпоративный SSO UUID.");

        string actor = authenticatedCorporateSsoUser.FindFirst(
            ClaimTypes.NameIdentifier)!.Value;
        using var request = CreateSignedRequest(
            trustedDocumentsOrigin, oneTimeLaunchId, actor, serviceHmacKey, now);
        using var handler = new HttpClientHandler
        {
            UseCookies = false, AllowAutoRedirect = false,
            UseDefaultCredentials = false, PreAuthenticate = false
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength is > MaxReceiptBytes)
            throw new UnauthorizedAccessException(
                "GUP-2 отклонил запуск или не подтвердил SSO-сессию.");

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[MaxReceiptBytes + 1];
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(
                buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        if (read == buffer.Length)
            throw new InvalidDataException("Ответ GUP-2 превышает лимит.");

        return ParseReceipt(Encoding.UTF8.GetString(buffer, 0, read),
            authenticatedCorporateSsoUser);
    }

    private static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } &&
        Guid.TryParseExact(value, "D", out var parsed) &&
        parsed != Guid.Empty &&
        parsed.ToString("D") == value;

    private static bool LowerHex(string? value, int len) =>
        value is not null && value.Length == len &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
