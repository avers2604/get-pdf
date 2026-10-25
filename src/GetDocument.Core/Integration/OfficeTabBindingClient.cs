using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GetDocument.Core.Integration;

public sealed record OfficeTabBindingReceipt(
    Guid DocumentId, int EditVersion, string Sha256,
    string Extension, string RegistrationNumber);

/// <summary>
/// One-shot SHA proof of a selected local Office tab. No document bytes,
/// cookies, user credentials, JWT, file path or UUID are sent to GUP-2.
/// The returned metadata is NOT an authorization to save any document.
/// </summary>
public static class OfficeTabBindingClient
{
    public const long MaxOfficeBytes = 150L * 1024 * 1024;

    public static bool IsValidCode(string? code) =>
        code is { Length: 43 } && code.All(c =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                 >= '0' and <= '9' or '-' or '_');

    public static bool IsOfficeFile(string? path) => path is not null &&
        (Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase));

    public static async Task<string> HashFileAsync(
        string path, long maxBytes = MaxOfficeBytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsOfficeFile(path) ||
            maxBytes <= 0 || maxBytes > 256L * 1024 * 1024)
            throw new ArgumentException("Нужен сохранённый локальный DOCX/XLSX.");
        string absolute = Path.GetFullPath(path);
        var before = new FileInfo(absolute);
        if (!before.Exists || before.Length <= 0 || before.Length > maxBytes)
            throw new InvalidDataException("Рабочая копия отсутствует либо слишком велика.");
        long length = before.Length;
        DateTime modified = before.LastWriteTimeUtc;
        await using var file = new FileStream(
            absolute, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != length)
            throw new IOException("Файл изменён перед привязкой.");
        byte[] digest = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        before.Refresh();
        if (!before.Exists || before.Length != length || before.LastWriteTimeUtc != modified)
            throw new IOException("Файл изменён во время вычисления контрольной суммы.");
        return Convert.ToHexStringLower(digest);
    }

    public static OfficeTabBindingReceipt ParseReceipt(
        string json, string expectedSha256, string extension)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var state) ||
            state.ValueKind != JsonValueKind.String || state.GetString() != "bound" ||
            !root.TryGetProperty("document_id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(id.GetString(), out Guid uuid) || uuid == Guid.Empty ||
            !root.TryGetProperty("edit_version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int editVersion) || editVersion < 0 ||
            !root.TryGetProperty("sha256", out var sha) ||
            sha.ValueKind != JsonValueKind.String ||
            !string.Equals(sha.GetString(), expectedSha256, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("extension", out var ext) ||
            ext.ValueKind != JsonValueKind.String ||
            !string.Equals(ext.GetString(), extension, StringComparison.OrdinalIgnoreCase) ||
            !IsOfficeFile("x" + extension))
            throw new InvalidDataException("Сервер не подтвердил идентичность открытой рабочей копии.");

        string number = "";
        if (root.TryGetProperty("reg_number", out var reg) &&
            reg.ValueKind == JsonValueKind.String)
            number = new string((reg.GetString() ?? "")
                .Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).Take(80).ToArray());
        return new(uuid, editVersion, expectedSha256.ToLowerInvariant(),
                   extension.ToLowerInvariant(), number);
    }

    public static async Task<OfficeTabBindingReceipt> BindAsync(
        string localFile, string code, OfficeTabBindingPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsValidCode(code))
            throw new ArgumentException("Неверный формат одноразового кода.");
        Uri url = policy.Destination();
        string fileHash = await HashFileAsync(localFile, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        string extension = Path.GetExtension(localFile).ToLowerInvariant();

        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            PreAuthenticate = false,
            UseDefaultCredentials = false,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("X-GetDocument-Bind-Code", code);
        request.Headers.TryAddWithoutValidation("X-GetDocument-File-SHA256", fileHash);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                "Не удалось связать вкладку с GUP-2. Обновите файл и получите новый код.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var bytes = new byte[4097];
        int used = 0;
        while (used < bytes.Length)
        {
            int count = await stream.ReadAsync(bytes.AsMemory(used), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) break;
            used += count;
        }
        if (used == bytes.Length)
            throw new InvalidDataException("Слишком большой ответ GUP-2.");
        string text = System.Text.Encoding.UTF8.GetString(bytes, 0, used);
        var receipt = ParseReceipt(text, fileHash, extension);

        // Local files can be modified while the request is in flight. The
        // association is valid only for the original verified SHA snapshot.
        if (!string.Equals(await HashFileAsync(localFile, cancellationToken: cancellationToken)
                .ConfigureAwait(false), fileHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Файл был изменён до окончания привязки.");
        return receipt;
    }
}
