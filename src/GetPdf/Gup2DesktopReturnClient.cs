using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace GetPdf;

/// <summary>
/// Uploads only through HTTPS with a five-minute one-time code issued inside
/// an authenticated GUP-2 browser session. No browser cookies, bearer JWT,
/// credentials, insecure TLS overrides, redirects or presigned SILO URLs.
/// Feature is disabled by default until a controlled server/client E2E gate.
/// </summary>
internal static class Gup2DesktopReturnClient
{
    internal const long MaximumPdfBytes = 256L * 1024 * 1024;

    internal static bool IsValidCode(string? code) =>
        code is { Length: 43 } &&
        code.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '0' and <= '9' or '-' or '_');

    internal static bool IsAllowedPdfSize(long size, long limit = MaximumPdfBytes) =>
        limit is > 0 and <= MaximumPdfBytes && size > 0 && size <= limit;

    internal static Uri Destination(WorkstationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.AllowGup2Portal || !policy.AllowGup2DirectReturn)
            throw new InvalidOperationException("Прямой возврат в GUP-2 запрещён политикой.");
        var origin = Gup2PortalLink.Validate(policy.Gup2PortalUrl);
        if (!origin.AbsolutePath.EndsWith("/documents/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Не указан корневой HTTPS-адрес раздела документов GUP-2.");
        return new Uri(origin, "getpdf/direct-return/");
    }

    internal static async Task UploadAsync(string localPdf, string code,
        WorkstationPolicy policy, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsValidCode(code))
            throw new ArgumentException("Неверный формат одноразового кода.");
        if (string.IsNullOrWhiteSpace(localPdf) ||
            !Path.GetExtension(localPdf).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите новый PDF-файл.");
        Uri uri = Destination(policy);

        // NewCopy writer always publishes a distinct file. The server also
        // rejects an unchanged copy of the WORM source by its SHA-256.
        string path = Path.GetFullPath(localPdf);
        var info = new FileInfo(path);
        if (!info.Exists || !IsAllowedPdfSize(info.Length))
            throw new InvalidDataException("Копия PDF отсутствует либо превышает лимит 256 МиБ.");

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(12) };
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var body = new MultipartFormDataContent();
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        body.Add(content, "file", Path.GetFileName(path));
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("X-GetPdf-Upload-Code", code);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Content = body;
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException(
                "GUP-2 отказал в приёме копии. Код мог истечь или права/версия изменились. " +
                "Получите новый код в карточке и проверьте файл.");
        // HTTP 201 is accepted_for_staging, NOT WORM promotion or a legal
        // new document revision. The user must inspect the document card.
    }
}
