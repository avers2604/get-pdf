using System.Globalization;
using System.Security.Claims;

namespace GetDocument.Collaboration;

/// <summary>
/// A pinned, signed corporate OIDC issuer + one application audience.
/// TLS/JWKS signature and issuer/audience validation are enforced by the
/// ASP.NET JwtBearer middleware, NOT merely by this claims policy.
/// </summary>
public sealed record EuroOfficeCorporateSsoSettings(
    string Authority, string Issuer, string Audience);

/// <summary>
/// Last-mile policy AFTER verified OIDC JWT authentication. The IdP MUST
/// configure the subject ("sub") to be exactly the immutable GUP-2 UUID.
/// Login names, email, role or upstream proxy headers are never user IDs.
/// </summary>
public static class EuroOfficeCorporateSsoPolicy
{
    private const long MaximumTokenSeconds = 15 * 60;
    private const long FutureSkewSeconds = 30;

    public static bool IsSsoEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal);

    public static EuroOfficeCorporateSsoSettings CreateSettings(
        string? authority, string? issuer, string? audience)
    {
        if (!SafeHttpsIdentityAuthority(authority, out var parsedAuthority) ||
            !SafeHttpsIdentityAuthority(issuer, out var parsedIssuer) ||
            !string.Equals(
                parsedAuthority!.AbsoluteUri.TrimEnd('/'),
                parsedIssuer!.AbsoluteUri.TrimEnd('/'),
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(audience) ||
            audience.Length > 128 ||
            audience.Any(char.IsWhiteSpace) ||
            audience.Any(char.IsControl))
            throw new ArgumentException(
                "OIDC issuer и authority должны совпадать и указывать на утверждённый HTTPS tenant; audience обязателен.");
        return new EuroOfficeCorporateSsoSettings(
            parsedAuthority.AbsoluteUri.TrimEnd('/'),
            parsedIssuer.AbsoluteUri.TrimEnd('/'),
            audience);
    }

    public static void BindVerifiedEmployee(ClaimsPrincipal? principal, DateTimeOffset now)
    {
        if (principal?.Identity is not ClaimsIdentity identity ||
            !identity.IsAuthenticated)
            throw new UnauthorizedAccessException("Корпоративная JWT-сессия отсутствует.");

        // MapInboundClaims=false in JwtBearer: "sub" stays as a signed JWT
        // claim. Ignore user names and any untrusted X-Forwarded-User header.
        var subjects = principal.FindAll("sub").ToArray();
        if (subjects.Length != 1 || !CanonicalUuid(subjects[0].Value))
            throw new UnauthorizedAccessException(
                "OIDC sub должен быть ровно одним каноническим UUID пользователя GUP-2.");
        string employee = subjects[0].Value;

        long issued = SingleEpoch(principal, "iat");
        long expiry = SingleEpoch(principal, "exp");
        long nowEpoch = now.ToUnixTimeSeconds();
        if (issued > nowEpoch + FutureSkewSeconds ||
            nowEpoch - issued > MaximumTokenSeconds ||
            expiry <= nowEpoch ||
            expiry <= issued ||
            expiry - issued > MaximumTokenSeconds)
            throw new UnauthorizedAccessException(
                "Время действия корпоративного OIDC токена не подходит для запуска редактора.");

        var names = principal.FindAll(ClaimTypes.NameIdentifier).ToArray();
        if (names.Length > 1 ||
            (names.Length == 1 &&
             !string.Equals(names[0].Value, employee, StringComparison.Ordinal)))
            throw new UnauthorizedAccessException("Конфликт SSO и GUP-2 идентичности.");

        // This claim is added ONLY after successful cryptographic JwtBearer
        // validation and strict subject/time checks. The existing launch
        // resolver and GUP-2 bridge both consume ClaimTypes.NameIdentifier.
        if (names.Length == 0)
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, employee));
    }

    private static long SingleEpoch(ClaimsPrincipal principal, string name)
    {
        var claims = principal.FindAll(name).ToArray();
        if (claims.Length != 1 || claims[0].Value is not { Length: 10 } value ||
            !value.All(char.IsAsciiDigit) ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out long stamp))
            throw new UnauthorizedAccessException("OIDC токен не имеет единственного проверяемого времени.");
        return stamp;
    }

    private static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } &&
        Guid.TryParseExact(value, "D", out var parsed) &&
        parsed != Guid.Empty && parsed.ToString("D") == value;

    private static bool SafeHttpsIdentityAuthority(string? text, out Uri? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim() ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.HostNameType != UriHostNameType.Dns ||
            uri.AbsolutePath.Contains("//", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsoluteUri.Contains('%') || uri.AbsoluteUri.Contains('\\'))
            return false;
        result = uri;
        return true;
    }
}
