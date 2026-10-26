using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace GetDocument.Collaboration;

/// <summary>
/// An Office output verified as coming from the explicitly pinned Document
/// Server network peer. Still requires independent GUP-2 ACL, CAS, AV/OOXML
/// and immutable-archive checks before any revision may be acknowledged.
/// </summary>
public sealed record PinnedEuroOfficeDownload(byte[] Bytes, string Sha256);

/// <summary>
/// Server-only, explicit-IP HTTPS transport. A callback URL cannot steer DNS:
/// TLS SNI and CA validation use the configured hostname, but the TCP socket
/// ALWAYS connects to the operator-supplied IP. Redirects and proxies are
/// disabled; no TLS validation bypass or network fallback exists.
/// </summary>
public static class PinnedEuroOfficeDownloader
{
    public const int MaximumBytes = 64 * 1024 * 1024;

    public static void ValidateDestination(
        EuroOfficeSaveCandidate candidate, Uri configuredOfficeOrigin,
        IPAddress approvedIp)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(approvedIp);
        OnlyOfficeUriPolicy.RequireHttpsOrigin(configuredOfficeOrigin);
        OnlyOfficeUriPolicy.RequireTrustedUrl(candidate.DownloadUrl, configuredOfficeOrigin);
        if (IPAddress.TryParse(configuredOfficeOrigin.IdnHost, out _) ||
            configuredOfficeOrigin.IdnHost.Length == 0 ||
            !string.Equals(candidate.DownloadUrl.IdnHost,
                configuredOfficeOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            candidate.DownloadUrl.Port != configuredOfficeOrigin.Port ||
            approvedIp.Equals(IPAddress.Any) || approvedIp.Equals(IPAddress.IPv6Any) ||
            approvedIp.IsIPv6Multicast || approvedIp.Equals(IPAddress.Broadcast))
            throw new UnauthorizedAccessException(
                "Нельзя загружать результат Euro-Office с незакреплённого адреса.");
    }

    public static async Task<PinnedEuroOfficeDownload> FetchAsync(
        EuroOfficeSaveCandidate candidate, Uri configuredOfficeOrigin,
        IPAddress approvedIp, CancellationToken cancellationToken = default)
    {
        ValidateDestination(candidate, configuredOfficeOrigin, approvedIp);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 2,
            ConnectCallback = async (context, cancel) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host,
                        configuredOfficeOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                    context.DnsEndPoint.Port != configuredOfficeOrigin.Port)
                    throw new UnauthorizedAccessException(
                        "Document Server направил соединение на другой адрес.");

                // The socket connects directly to the approved IP. The
                // HTTPS handler still authenticates configuredOfficeOrigin's
                // hostname through the standard trusted-CA TLS handshake.
                var socket = new Socket(approvedIp.AddressFamily,
                    SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(approvedIp, configuredOfficeOrigin.Port), cancel)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler)
        { Timeout = TimeSpan.FromMinutes(3) };

        using var request = new HttpRequestMessage(
            HttpMethod.Get, candidate.DownloadUrl);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAndHashAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PinnedEuroOfficeDownload> ReadAndHashAsync(
        HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new UnauthorizedAccessException(
                "Сервер редактирования не выдал подтверждённую Office-копию.");

        string? mime = response.Content.Headers.ContentType?.MediaType;
        if (mime is not (
            "application/octet-stream" or
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" or
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"))
            throw new InvalidDataException("Неожиданный MIME результата Euro-Office.");
        if (response.Content.Headers.ContentEncoding.Count > 0 ||
            response.Content.Headers.ContentRange is not null ||
            response.Content.Headers.ContentLength is not long length ||
            length <= 0 || length > MaximumBytes)
            throw new InvalidDataException("Результат Euro-Office должен иметь фиксированный допустимый размер.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream((int)length);
        byte[] buffer = new byte[64 * 1024];
        while (output.Length < length)
        {
            int count = await input.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - output.Length)),
                cancellationToken).ConfigureAwait(false);
            if (count <= 0)
                throw new EndOfStreamException("Неполный файл от Euro-Office.");
            output.Write(buffer, 0, count);
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false) != 0)
            throw new InvalidDataException("Фактический размер файла превышает Content-Length.");

        var contents = output.ToArray();
        string digest = Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();
        return new PinnedEuroOfficeDownload(contents, digest);
    }
}
