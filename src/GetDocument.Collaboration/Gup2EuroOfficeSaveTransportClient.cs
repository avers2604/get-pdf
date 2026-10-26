using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Internal GUP-2 transaction receipt. "Committed" is only true when the
/// server answered with a matching SHA and persisted positive revision.
/// This does NOT identify or authenticate an individual editing employee.
/// </summary>
public sealed record Gup2EuroOfficeSaveReceipt(
    int EditVersion, string Sha256, bool Committed);

/// <summary>
/// Server-to-server transport for a previously authenticated and downloaded
/// Euro-Office output. The GUP-2 grant is issued by its trusted SSO session
/// lifecycle, not by this client or by a browser. A separate HMAC key signs
/// the exact operation, grant, employee, timestamp, bytes hash, callback
/// fingerprint and length. No callback URLs or private paths are followed.
/// </summary>
public static class Gup2EuroOfficeSaveTransportClient
{
    public const int MaxBytes = 64 * 1024 * 1024;
    private const int MaxReceiptBytes = 4096;
    private const string Purpose = "getdocument/eurooffice/save";

    public static HttpRequestMessage CreateSignedRequest(
        Uri trustedDocumentsOrigin, string serverOnlyGrant, string employeeId,
        byte[] signingKey, DateTimeOffset now,
        ReadOnlyMemory<byte> verifiedOfficeBytes, string callbackFingerprint)
    {
        ArgumentNullException.ThrowIfNull(trustedDocumentsOrigin);
        if (!trustedDocumentsOrigin.IsAbsoluteUri ||
            trustedDocumentsOrigin.Scheme != Uri.UriSchemeHttps ||
            trustedDocumentsOrigin.AbsolutePath != "/documents/" ||
            !string.IsNullOrEmpty(trustedDocumentsOrigin.UserInfo) ||
            !string.IsNullOrEmpty(trustedDocumentsOrigin.Fragment) ||
            !string.IsNullOrEmpty(trustedDocumentsOrigin.Query) ||
            trustedDocumentsOrigin.AbsoluteUri.Contains('%'))
            throw new ArgumentException("Требуется утверждённый HTTPS адрес GUP-2.");
        if (!LowerHex(serverOnlyGrant, 64) ||
            !Guid.TryParseExact(employeeId, "D", out Guid parsedEmployee) ||
            parsedEmployee == Guid.Empty ||
            parsedEmployee.ToString("D") != employeeId ||
            !LowerHex(callbackFingerprint, 64) ||
            verifiedOfficeBytes.Length is <= 0 or > MaxBytes ||
            signingKey is null || signingKey.Length < 32)
            throw new ArgumentException(
                "Неверный server-only grant, сотрудник, callback SHA или лимит Office-копии.");

        string sha = Convert.ToHexString(
            SHA256.HashData(verifiedOfficeBytes.Span)).ToLowerInvariant();
        string timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string signed = "POST\n" + Purpose + "\n" + serverOnlyGrant + "\n" +
                        employeeId + "\n" + timestamp + "\n" + sha + "\n" +
                        callbackFingerprint + "\n" +
                        verifiedOfficeBytes.Length.ToString(CultureInfo.InvariantCulture);
        string proof = Convert.ToHexString(HMACSHA256.HashData(
            signingKey, Encoding.ASCII.GetBytes(signed))).ToLowerInvariant();

        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(trustedDocumentsOrigin, Purpose + "/"));
        request.Content = new ByteArrayContent(verifiedOfficeBytes.ToArray());
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Save-Code", serverOnlyGrant);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-User", employeeId);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-File-Sha256", sha);
        request.Headers.TryAddWithoutValidation(
            "X-Get-Eurooffice-Callback-Fingerprint", callbackFingerprint);
        request.Headers.TryAddWithoutValidation("X-Get-Eurooffice-Signature", proof);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    public static Gup2EuroOfficeSaveReceipt ParseReceipt(
        HttpStatusCode httpStatus, string json, string expectedSha256)
    {
        if (httpStatus is not (HttpStatusCode.Created or HttpStatusCode.OK))
            throw new UnauthorizedAccessException(
                "GUP-2 не подтвердил новую рабочую редакцию.");
        if (!LowerHex(expectedSha256, 64) ||
            string.IsNullOrWhiteSpace(json) || json.Length > MaxReceiptBytes)
            throw new InvalidDataException("Неприемлемый ответ GUP-2.");

        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("GUP-2 прислал неожиданный тип ответа.");

            var props = root.EnumerateObject().Select(x => x.Name).ToArray();
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "status", "edit_version", "sha256" };
            if (props.Length != 3 || props.Distinct(StringComparer.Ordinal).Count() != 3 ||
                props.Any(x => !allowed.Contains(x)))
                throw new InvalidDataException("Нельзя принимать лишние поля от GUP-2.");

            string? status = root.GetProperty("status").GetString();
            bool first = httpStatus == HttpStatusCode.Created;
            if (status != (first ? "working_copy_saved" : "already_committed"))
                throw new InvalidDataException("GUP-2 не подтверждает фактическую фиксацию версии.");

            var revision = root.GetProperty("edit_version");
            string? digest = root.GetProperty("sha256").GetString();
            if (revision.ValueKind != JsonValueKind.Number ||
                !revision.TryGetInt32(out int value) || value <= 0 ||
                digest != expectedSha256 || !LowerHex(digest, 64))
                throw new InvalidDataException("Редакция или SHA-256 не подтверждены сервером.");

            return new Gup2EuroOfficeSaveReceipt(value, digest!, true);
        }
        catch (Exception exception) when (exception is JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Неверный JSON ответа сохранения GUP-2.", exception);
        }
    }

    public static async Task<Gup2EuroOfficeSaveReceipt> SubmitAsync(
        Uri origin, string grant, string employeeId,
        byte[] serverHmacKey, DateTimeOffset now,
        ReadOnlyMemory<byte> alreadyVerifiedOfficeBytes,
        string signedCallbackFingerprint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = CreateSignedRequest(
            origin, grant, employeeId, serverHmacKey, now,
            alreadyVerifiedOfficeBytes, signedCallbackFingerprint);

        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            UseDefaultCredentials = false,
            PreAuthenticate = false
        };
        using var client = new HttpClient(handler)
        { Timeout = TimeSpan.FromMinutes(3) };
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
            throw new UnauthorizedAccessException(
                "GUP-2 отклонил запись или обнаружил конфликт версий.");
        if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength is > MaxReceiptBytes)
            throw new InvalidDataException("Неверный формат подтверждения GUP-2.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[MaxReceiptBytes + 1];
        int received = 0;
        while (received < buffer.Length)
        {
            int count = await stream.ReadAsync(
                buffer.AsMemory(received), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            received += count;
        }
        if (received == buffer.Length)
            throw new InvalidDataException("Ответ GUP-2 превышает лимит.");

        string expectedSha = Convert.ToHexString(
            SHA256.HashData(alreadyVerifiedOfficeBytes.Span)).ToLowerInvariant();
        return ParseReceipt(
            response.StatusCode, Encoding.UTF8.GetString(buffer, 0, received), expectedSha);
    }

    private static bool LowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
