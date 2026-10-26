using System.Net;
using System.Security.Cryptography;

namespace GetDocument.Collaboration;

/// <summary>
/// Internal GUP-2 immutable working-revision reader. The returned bytes are
/// verified against an expected SHA-256 BEFORE they may reach an editor.
/// This code must only run inside the trusted server, never Avalonia or WebView.
/// It deliberately is NOT an ITrustedEuroOfficeLaunchStore or callback sink.
/// </summary>
public static class Gup2EuroOfficeContentClient
{
    // Until encrypted temporary storage and streaming audit are accepted,
    // keep plaintext only in a bounded process-memory buffer.
    public const int DefaultMaxBytes = 64 * 1024 * 1024;

    public static HttpRequestMessage CreateSignedRequest(
        Uri documentsOrigin, string oneUseGrant, string employeeId,
        byte[] serviceKey, DateTimeOffset now) =>
        Gup2EuroOfficeAttestationClient.CreateSignedContentRequest(
            documentsOrigin, oneUseGrant, employeeId, serviceKey, now);

    public static void EnsureContentStatus(HttpStatusCode status)
    {
        if (status != HttpStatusCode.OK)
            throw new UnauthorizedAccessException(
                "GUP-2 не подтвердил выдачу проверенной рабочей копии.");
    }

    public static byte[] VerifyDownloadedContent(
        ReadOnlyMemory<byte> bytes, string expectedSha256, int maxBytes = DefaultMaxBytes)
    {
        if (expectedSha256 is not { Length: 64 } ||
            !expectedSha256.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Ожидается SHA-256 из подтверждённого GUP-2 контракта.",
                nameof(expectedSha256));
        if (maxBytes <= 0 || maxBytes > DefaultMaxBytes ||
            bytes.Length <= 0 || bytes.Length > maxBytes)
            throw new InvalidDataException("Недопустимый размер Office-копии.");

        var actual = SHA256.HashData(bytes.Span);
        var expected = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException(
                "Полученные байты отличаются от закреплённой редакции GUP-2.");
        return bytes.ToArray();
    }

    public static async Task<byte[]> DownloadVerifiedAsync(
        Uri documentsOrigin, string oneUseGrant, string employeeId,
        byte[] serviceKey, string expectedSha256, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = CreateSignedRequest(
            documentsOrigin, oneUseGrant, employeeId, serviceKey, now);

        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            PreAuthenticate = false,
            UseDefaultCredentials = false,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return await ReadVerifiedResponseAsync(response, expectedSha256, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Independently testable transport validation. Reject partial responses,
    /// redirects, unexpected media types and tampered bytes before release.
    /// </summary>
    public static async Task<byte[]> ReadVerifiedResponseAsync(
        HttpResponseMessage response, string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureContentStatus(response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType != "application/octet-stream")
            throw new InvalidDataException("Сервер прислал неожиданный тип содержимого.");

        long? advertisedLength = response.Content.Headers.ContentLength;
        if (advertisedLength is null or <= 0 or > DefaultMaxBytes)
            throw new InvalidDataException("Отсутствует допустимый размер подтверждённой копии.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var sink = new MemoryStream((int)advertisedLength.Value);
        byte[] buffer = new byte[64 * 1024];
        while (sink.Length < advertisedLength.Value)
        {
            int toRead = (int)Math.Min(buffer.Length, advertisedLength.Value - sink.Length);
            int count = await source.ReadAsync(
                buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException("GUP-2 прервал выдачу Office-копии.");
            await sink.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false) != 0)
            throw new InvalidDataException("Количество байт Office-копии превышает заявленное.");

        // Never expose an unverified byte[] to the editor.
        return VerifyDownloadedContent(sink.ToArray(), expectedSha256);
    }
}
