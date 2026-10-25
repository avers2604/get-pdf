using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GetDocument.Core.Integration;

/// <summary>
/// Sends an explicitly saved DOCX/XLSX working copy with a single-use code.
/// No cookies, JWT, URL token, automatic redirect or certificate bypass.
/// A 201 only means GUP-2 accepted a new working copy, not legal publication.
/// </summary>
public static class OfficeReturnClient
{
    public static bool IsValidCode(string? code) =>
        code is { Length: 43 } &&
        code.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                     or >= '0' and <= '9' or '-' or '_');

    public static bool IsSupportedCopy(string? path) =>
        path is not null &&
        (Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase));

    public static bool IsAllowedSize(long length, long maxBytes) =>
        maxBytes > 0 && maxBytes <= OfficeReturnPolicy.AbsoluteMaxUploadBytes &&
        length > 0 && length <= maxBytes;

    public static async Task<OfficeReturnReceipt> UploadAsync(
        string savedPath, string oneTimeCode, OfficeReturnPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidCode(oneTimeCode))
            throw new ArgumentException("Одноразовый код GUP-2 имеет неверный формат.");
        if (string.IsNullOrWhiteSpace(savedPath) || !IsSupportedCopy(savedPath) ||
            savedPath.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Выберите сохранённую рабочую копию DOCX/XLSX.");

        Uri destination = policy.Destination();
        string absolute = Path.GetFullPath(savedPath);
        var information = new FileInfo(absolute);
        if (!information.Exists || !IsAllowedSize(information.Length, policy.MaxUploadBytes))
            throw new InvalidDataException("Рабочая копия отсутствует или превышает корпоративный лимит.");

        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            PreAuthenticate = false,
            UseDefaultCredentials = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(12) };
        await using var file = new FileStream(absolute, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var body = new MultipartFormDataContent();
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            Path.GetExtension(absolute).Equals(".docx", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        body.Add(content, "file", Path.GetFileName(absolute));
        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        request.Headers.TryAddWithoutValidation("X-GetDocument-Upload-Code", oneTimeCode);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Content = body;
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException(
                "GUP-2 отклонил редакцию. Проверьте права и версию документа, затем получите новый код.");

        // Even a compromised/misconfigured endpoint cannot claim success
        // through a redirect or unlimited response payload.
        await using var resultStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] bytes = new byte[4097];
        int used = 0;
        while (used < bytes.Length)
        {
            int read = await resultStream.ReadAsync(bytes.AsMemory(used), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            used += read;
        }
        if (used == bytes.Length)
            throw new InvalidDataException("Некорректный ответ GUP-2.");

        using var responseJson = JsonDocument.Parse(bytes.AsMemory(0, used));
        var root = responseJson.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var status) ||
            status.GetString() != "working_copy_saved" ||
            !root.TryGetProperty("edit_version", out var revision) ||
            !revision.TryGetInt32(out int newVersion) || newVersion < 0 ||
            !root.TryGetProperty("sha256", out var sha) ||
            sha.ValueKind != JsonValueKind.String ||
            !IsSha256(sha.GetString()))
            throw new InvalidDataException("GUP-2 не подтвердил сохранение рабочей копии.");

        return new OfficeReturnReceipt(newVersion, sha.GetString()!);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c =>
            c is >= 'a' and <= 'f' or >= 'A' and <= 'F' or >= '0' and <= '9');
}

public sealed record OfficeReturnReceipt(int EditVersion, string Sha256);
