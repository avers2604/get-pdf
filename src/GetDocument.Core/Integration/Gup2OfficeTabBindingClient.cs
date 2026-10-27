using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GetDocument.Core.Integration;

/// <summary>
/// Non-authorizing receipt from GUP-2's existing GETDOCUMENT_TAB_BIND endpoint.
/// This proves only that a local DOCX/XLSX SHA matched a working copy at one
/// moment. It MUST NOT be used as a bearer token, URL or edit permission.
/// </summary>
public sealed record Gup2OfficeTabBinding(
    Guid DocumentId, int EditVersion, string Sha256, string Extension,
    string RegistrationNumber);

/// <summary>
/// Redeems the existing, independently issued GUP-2 tab-binding capability.
/// Never sends file bytes or browser cookies, never follows redirects.
/// The server checks AD/ACL, WORM, revocation, revision and SHA independently.
/// </summary>
public static class Gup2OfficeTabBindingClient
{
    private const int MaxResponseBytes = 4096;

    public static void EnsureBoundStatus(HttpStatusCode status)
    {
        if (status != HttpStatusCode.OK)
            throw new InvalidOperationException(
                "Привязка GUP-2 не подтверждена (код недействителен, права отозваны или версия изменилась).");
    }

    public static Gup2OfficeTabBinding ParseReceipt(
        string json, string localSha256, string localExtension)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxResponseBytes ||
            !ValidSha(localSha256) || !SupportedExtension(localExtension))
            throw new InvalidDataException("Неверные данные привязки вкладки.");

        try
        {
            using var response = JsonDocument.Parse(json);
            var root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Неверная структура ответа GUP-2.");

            // A metadata-binding response never contains credentials, paths
            // or revision bytes. Refuse unexpected fields rather than
            // accidentally treating a later richer response as authorization.
            var fields = root.EnumerateObject().Select(x => x.Name).ToArray();
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "status", "document_id", "edit_version", "sha256",
                "extension", "reg_number"
            };
            if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length ||
                fields.Any(x => !allowed.Contains(x)) ||
                !root.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String || status.GetString() != "bound" ||
                !root.TryGetProperty("document_id", out var doc) ||
                doc.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(doc.GetString(), "D", out Guid documentId) ||
                documentId == Guid.Empty ||
                !root.TryGetProperty("edit_version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int editVersion) || editVersion < 0 ||
                !root.TryGetProperty("sha256", out var hash) ||
                hash.ValueKind != JsonValueKind.String || !ValidSha(hash.GetString()) ||
                !root.TryGetProperty("extension", out var extension) ||
                extension.ValueKind != JsonValueKind.String ||
                !SupportedExtension(extension.GetString()) ||
                !root.TryGetProperty("reg_number", out var registration) ||
                registration.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("GUP-2 не подтвердил привязку рабочей копии.");

            string? number = registration.GetString();
            if (number is null || number.Length > 180 ||
                number.Any(ch => char.IsControl(ch) || char.IsSurrogate(ch) ||
                    CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format) ||
                !string.Equals(hash.GetString(), localSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(extension.GetString(), localExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Хеш, тип или идентификатор GUP-2 не соответствует открытой копии.");

            return new Gup2OfficeTabBinding(documentId, editVersion,
                hash.GetString()!.ToLowerInvariant(),
                extension.GetString()!.ToLowerInvariant(), number);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Некорректный JSON ответа GUP-2.", ex);
        }
    }

    public static async Task<Gup2OfficeTabBinding> RedeemAsync(
        string savedPath, string oneTimeCode, OfficeReturnPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OfficeReturnClient.IsValidCode(oneTimeCode))
            throw new ArgumentException("Одноразовый код привязки имеет неправильный формат.",
                nameof(oneTimeCode));
        if (string.IsNullOrWhiteSpace(savedPath) || savedPath.Contains("://", StringComparison.Ordinal) ||
            !OfficeReturnClient.IsSupportedCopy(savedPath))
            throw new ArgumentException("Для привязки требуется локальный DOCX/XLSX.", nameof(savedPath));

        Uri destination = policy.BindingDestination();
        string path = Path.GetFullPath(savedPath);
        var information = new FileInfo(path);
        if (!information.Exists || !OfficeReturnClient.IsAllowedSize(information.Length, policy.MaxUploadBytes))
            throw new InvalidDataException("Рабочая копия отсутствует либо превышает лимит.");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] sha = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (stream.Length != information.Length)
            throw new InvalidDataException("Локальная копия изменилась при вычислении SHA-256.");
        string localSha = Convert.ToHexString(sha).ToLowerInvariant();

        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            PreAuthenticate = false,
            UseDefaultCredentials = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        request.Headers.TryAddWithoutValidation("X-GetDocument-Bind-Code", oneTimeCode);
        request.Headers.TryAddWithoutValidation("X-GetDocument-File-SHA256", localSha);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        EnsureBoundStatus(response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var bytes = new byte[MaxResponseBytes + 1];
        int used = 0;
        while (used < bytes.Length)
        {
            int count = await body.ReadAsync(bytes.AsMemory(used), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) break;
            used += count;
        }
        if (used == bytes.Length)
            throw new InvalidDataException("Ответ GUP-2 слишком большой.");

        return ParseReceipt(System.Text.Encoding.UTF8.GetString(bytes, 0, used),
            localSha, Path.GetExtension(path));
    }

    private static bool SupportedExtension(string? extension) =>
        extension is not null &&
        (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase));

    private static bool ValidSha(string? hash) =>
        hash is { Length: 64 } && hash.All(ch =>
            ch is >= 'a' and <= 'f' or >= 'A' and <= 'F' or >= '0' and <= '9');
}
