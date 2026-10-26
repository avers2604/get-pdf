using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Cryptographically unpredictable 128-bit one-time launch handle.
/// The enterprise store MUST consume this handle atomically and bind it
/// to the authenticated user, document and authorization window.
/// </summary>
public static class EuroOfficeLaunchId
{
    public static string New() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsValid(string? id) =>
        id is { Length: 32 } && id.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Issued ONLY by a trusted GUP-2/SSO backend after authentication, ACL lookup
/// and document/revision authorization. The host MUST consume its random
/// 128-bit launch identifier once, not retrieve a reusable session by file ID.
/// </summary>
public sealed record EuroOfficeAuthorizedLaunch(
    OnlyOfficeSessionOptions Session,
    DateTimeOffset ExpiresAtUtc,
    bool ImmutableRevisionSinkReady);

/// <summary>
/// This contract deliberately has no user-controlled session URL or document
/// path. The future enterprise implementation MUST bind the nonce to the
/// authenticated ClaimsPrincipal and atomically consume it once.
/// </summary>
public interface ITrustedEuroOfficeLaunchStore
{
    ValueTask<EuroOfficeAuthorizedLaunch?> ConsumeOnceAsync(
        string launchId, ClaimsPrincipal authenticatedUser, CancellationToken cancellationToken);
}

/// <summary>
/// Safe default until a real corporate authorization/revision provider exists.
/// </summary>
public sealed class DenyAllEuroOfficeLaunchStore : ITrustedEuroOfficeLaunchStore
{
    public ValueTask<EuroOfficeAuthorizedLaunch?> ConsumeOnceAsync(
        string launchId, ClaimsPrincipal authenticatedUser, CancellationToken cancellationToken) =>
        ValueTask.FromResult<EuroOfficeAuthorizedLaunch?>(null);
}

public sealed record EuroOfficeLaunchPageResult(
    string Html, string ContentSecurityPolicy);

/// <summary>
/// Browser bootstrap for a trusted Euro-Office Document Server. No arbitrary
/// HTML/JS is accepted from user input. The JWT is held in the editor config
/// body, NEVER appended to the launch URL or logged by the host.
/// This page is not an authorization mechanism: enterprise SSO, nonce
/// consumption and permissions must be implemented by the caller.
/// </summary>
public static class EuroOfficeLaunchPage
{
    private static readonly TimeSpan MaxLaunchTtl = TimeSpan.FromMinutes(5);

    public static EuroOfficeLaunchPageResult Render(
        EuroOfficeAuthorizedLaunch authorized,
        Uri trustedOfficeOrigin,
        byte[] signingKey,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(authorized);
        ArgumentNullException.ThrowIfNull(authorized.Session);
        OnlyOfficeUriPolicy.RequireHttpsOrigin(trustedOfficeOrigin);
        if (!string.Equals(
                authorized.Session.DocumentServerOrigin.GetLeftPart(UriPartial.Authority),
                trustedOfficeOrigin.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Сервер редактора не совпадает с доверенным origin.",
                nameof(trustedOfficeOrigin));
        if (authorized.ExpiresAtUtc <= now ||
            authorized.ExpiresAtUtc - now > MaxLaunchTtl)
            throw new UnauthorizedAccessException(
                "Одноразовый запуск Euro-Office истёк или имеет недопустимый срок.");

        var bootstrap = EuroOfficeSessionFactory.Create(
            authorized.Session, signingKey, authorized.ImmutableRevisionSinkReady);

        // System.Text.Json default JavaScriptEncoder escapes <, >, &, quotes and
        // script terminators. JSON.parse of the serialized JSON *string*
        // provides an extra escaping boundary for untrusted document titles.
        string escapedEditorJson = JsonSerializer.Serialize(bootstrap.ConfigurationJson);
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        string officeAuthority = trustedOfficeOrigin.GetLeftPart(UriPartial.Authority);
        string scriptUrl = HtmlEncoder.Default.Encode(
            new Uri(trustedOfficeOrigin,
                "web-apps/apps/api/documents/api.js").AbsoluteUri);

        // The CSP is deliberately scoped to the pinned office origin rather
        // than wildcard sources. An iframe may load its own Office resources
        // under the Document Server's separate origin and CSP.
        string csp =
            "default-src 'none'; " +
            "base-uri 'none'; object-src 'none'; form-action 'none'; " +
            "frame-ancestors 'none'; " +
            $"script-src 'nonce-{nonce}' {officeAuthority}; " +
            $"frame-src {officeAuthority}; " +
            $"connect-src {officeAuthority}; " +
            $"img-src data: {officeAuthority}; " +
            $"font-src {officeAuthority}; " +
            $"style-src 'unsafe-inline' {officeAuthority}; " +
            "upgrade-insecure-requests";

        string html = $$"""
            <!doctype html>
            <html lang="ru">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta name="referrer" content="no-referrer">
              <title>ГЭТ Документы — Euro-Office</title>
              <style>html,body,#euro-office-editor{width:100%;height:100%;margin:0;padding:0;overflow:hidden}</style>
              <script src="{{scriptUrl}}"></script>
            </head>
            <body>
              <div id="euro-office-editor" aria-label="Редактор документов ГЭТ"></div>
              <script nonce="{{nonce}}">
              (() => {
                "use strict";
                const config = JSON.parse({{escapedEditorJson}});
                new DocsAPI.DocEditor("euro-office-editor", config);
              })();
              </script>
            </body>
            </html>
            """;

        return new EuroOfficeLaunchPageResult(html, csp);
    }
}
