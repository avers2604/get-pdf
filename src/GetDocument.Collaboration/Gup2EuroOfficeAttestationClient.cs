using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Only a read-only, single-use identity attestation from GUP-2.
/// Not a Euro-Office launch ticket, bearer credential or content URL.
/// </summary>
public sealed record Gup2EuroOfficeAttestation(
    Guid DocumentId,
    string RevisionId,
    string EmployeeId,
    string Sha256,
    string Extension,
    bool CanEdit = false);

/// <summary>
/// Server-to-server GUP-2 metadata attestation. Does NOT implement
/// ITrustedEuroOfficeLaunchStore: no ACL-backed document bytes or trusted
/// callbacks exist yet, so no live Office session can be opened.
/// </summary>
public static class Gup2EuroOfficeAttestationClient
{
    private const int MaxResponseBytes = 4096;
    private const string ConsumePath = "getdocument/eurooffice/consume";

    public static HttpRequestMessage CreateSignedRequest(
        Uri trustedDocumentsOrigin,
        string opaqueGrant,
        string employeeId,
        byte[] sharedSecret,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trustedDocumentsOrigin);
        if (!trustedDocumentsOrigin.IsAbsoluteUri ||
            trustedDocumentsOrigin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(trustedDocumentsOrigin.UserInfo) ||
            !string.IsNullOrEmpty(trustedDocumentsOrigin.Query) ||
            !string.IsNullOrEmpty(trustedDocumentsOrigin.Fragment) ||
            trustedDocumentsOrigin.AbsolutePath != "/documents/" ||
            trustedDocumentsOrigin.AbsoluteUri.Contains('%'))
            throw new ArgumentException("Разрешён только корпоративный HTTPS /documents/ origin.");

        if (!EuroOfficeLaunchId.IsValid(opaqueGrant))
            throw new ArgumentException("Неверный формат одноразового Euro-Office grant.");
        if (!CanonicalUuid(employeeId))
            throw new ArgumentException("Необходим неизменяемый UUID сотрудника GUP-2.");
        if (sharedSecret is null || sharedSecret.Length < 32)
            throw new ArgumentException("Неверный сервисный HMAC ключ.", nameof(sharedSecret));

        string stamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string canonical = "POST\n" + ConsumePath + "\n" + opaqueGrant +
                           "\n" + employeeId + "\n" + stamp;
        string signature = Convert.ToHexString(HMACSHA256.HashData(
            sharedSecret, Encoding.ASCII.GetBytes(canonical))).ToLowerInvariant();

        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(trustedDocumentsOrigin, ConsumePath + "/"));
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Nonce", opaqueGrant);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-User", employeeId);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Timestamp", stamp);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Signature", signature);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    public static void EnsureSuccessfulStatus(HttpStatusCode status)
    {
        if (status != HttpStatusCode.OK)
            throw new UnauthorizedAccessException(
                "GUP-2 не подтвердил одноразовое разрешение Euro-Office.");
    }

    public static Gup2EuroOfficeAttestation ParseAttestation(
        string json, string expectedEmployeeId)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxResponseBytes ||
            !CanonicalUuid(expectedEmployeeId))
            throw new InvalidDataException("Неправильный ответ GUP-2.");
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            var permitted = new HashSet<string>(StringComparer.Ordinal)
            {
                "status", "document_id", "revision_id", "user_id",
                "sha256", "extension", "can_edit"
            };
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != permitted.Count ||
                root.EnumerateObject().Any(x => !permitted.Contains(x.Name)) ||
                root.EnumerateObject().Select(x => x.Name).Distinct().Count() != permitted.Count ||
                root.GetProperty("status").GetString() != "attested" ||
                root.GetProperty("can_edit").ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Неподдерживаемый ответ аттестации Euro-Office.");

            string? document = root.GetProperty("document_id").GetString();
            string? user = root.GetProperty("user_id").GetString();
            string? revision = root.GetProperty("revision_id").GetString();
            string? digest = root.GetProperty("sha256").GetString();
            string? extension = root.GetProperty("extension").GetString();

            if (!CanonicalUuid(document) ||
                !Guid.TryParseExact(document, "D", out Guid docId) ||
                !string.Equals(user, expectedEmployeeId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(revision) || revision.Length > 24 ||
                !revision.All(char.IsAsciiDigit) ||
                digest is not { Length: 64 } ||
                !digest.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                extension is not (".docx" or ".xlsx"))
                throw new InvalidDataException("Метаданные GUP-2 не прошли проверку.");

            return new Gup2EuroOfficeAttestation(docId, revision, user!, digest,
                extension);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Неверный JSON от GUP-2.", ex);
        }
    }

    public static async Task<Gup2EuroOfficeAttestation> ConsumeOnceAsync(
        Uri trustedDocumentsOrigin,
        string grant,
        string employeeId,
        byte[] sharedSecret,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = CreateSignedRequest(
            trustedDocumentsOrigin, grant, employeeId, sharedSecret, now);
        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            PreAuthenticate = false,
            UseDefaultCredentials = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        EnsureSuccessfulStatus(response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] bytes = new byte[MaxResponseBytes + 1];
        int used = 0;
        while (used < bytes.Length)
        {
            int received = await stream.ReadAsync(bytes.AsMemory(used), cancellationToken)
                .ConfigureAwait(false);
            if (received == 0) break;
            used += received;
        }
        if (used == bytes.Length)
            throw new InvalidDataException("Неправдоподобно большой ответ GUP-2.");
        return ParseAttestation(Encoding.UTF8.GetString(bytes, 0, used), employeeId);
    }

    private static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } &&
        Guid.TryParseExact(value, "D", out _) &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
}
