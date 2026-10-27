using System.Security.Claims;

namespace GetDocument.Collaboration;

/// <summary>
/// Explicit, confidential enterprise OIDC client configuration.
/// No discovered issuer override, HTTP metadata, forwarded-identity scheme or
/// configurable "which user claim" fallback is accepted. Only ID-token claim
/// gup_user_id from the pinned issuer can identify a GUP-2 employee.
/// </summary>
public sealed class EuroOfficeOidcConfiguration
{
    private EuroOfficeOidcConfiguration(string issuer, string clientId, string clientSecret)
    {
        Issuer = issuer;
        ClientId = clientId;
        ClientSecret = clientSecret;
        MetadataAddress = new Uri(
            issuer.TrimEnd('/') + "/.well-known/openid-configuration",
            UriKind.Absolute);
        IssuerAuthority = new Uri(issuer, UriKind.Absolute)
            .GetLeftPart(UriPartial.Authority);
    }

    public string Issuer { get; }
    public string ClientId { get; }

    // Server-only secret, injected from enterprise secret store. Never log.
    public string ClientSecret { get; }
    public Uri MetadataAddress { get; }
    private string IssuerAuthority { get; }

    /// <summary>
    /// Disabled mode never requires external identity-service configuration.
    /// Enabled mode refuses implicit defaults, placeholders and non-HTTPS IdP.
    /// </summary>
    public static EuroOfficeOidcConfiguration? Create(
        bool enabled, string? issuer, string? clientId, string? clientSecret)
    {
        if (!enabled)
            return null;

        if (string.IsNullOrWhiteSpace(issuer) || issuer != issuer.Trim() ||
            issuer.Length > 2048 ||
            !Uri.TryCreate(issuer, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host.Length == 0 || !uri.IsDefaultPort && uri.Port == 0 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            issuer.Contains('%') || issuer.Contains('\\') ||
            issuer.Contains("/../", StringComparison.Ordinal) ||
            issuer.Contains("/./", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Корпоративный OIDC issuer должен быть фиксированным HTTPS URL.");

        if (string.IsNullOrWhiteSpace(clientId) ||
            clientId.Length is < 3 or > 128 ||
            !(char.IsAsciiLetterOrDigit(clientId[0])) ||
            !clientId.All(c =>
                char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new InvalidOperationException(
                "Не задан корректный client_id корпоративного OIDC.");

        if (string.IsNullOrWhiteSpace(clientSecret) ||
            clientSecret.Length < 32 || clientSecret.Length > 4096 ||
            clientSecret.Any(char.IsControl))
            throw new InvalidOperationException(
                "Client secret корпоративного OIDC отсутствует или слишком короткий.");

        return new EuroOfficeOidcConfiguration(issuer, clientId, clientSecret);
    }

    /// <summary>
    /// Avoid following issuer-advertised authorization URLs at another host.
    /// IdP's JWKS and token endpoints still require separate local mTLS/CA
    /// and metadata validation in integration tests.
    /// </summary>
    public bool IsTrustedAuthorizationEndpoint(Uri? authorizationEndpoint) =>
        authorizationEndpoint is { IsAbsoluteUri: true } &&
        authorizationEndpoint.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(authorizationEndpoint.UserInfo) &&
        authorizationEndpoint.GetLeftPart(UriPartial.Authority)
            .Equals(IssuerAuthority, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"EuroOfficeOidcConfiguration(Issuer={Issuer}, ClientId={ClientId}, Secret=REDACTED)";
}

/// <summary>
/// The OIDC framework MUST have validated ID-token signature (JWKS), issuer,
/// audience, lifetime and nonce/state before invoking this mapper.
/// It intentionally does NOT validate tokens itself and must never be called
/// with arbitrary HTTP headers or browser-supplied claims.
/// </summary>
public static class EuroOfficeOidcIdentityMapper
{
    public const string Gup2UserIdClaim = "gup_user_id";
    public const string Scheme = "CorporateOidc";

    public static ClaimsPrincipal MapAfterTokenValidation(ClaimsPrincipal? validatedOidcToken)
    {
        if (validatedOidcToken is null ||
            validatedOidcToken.Identities.Count() != 1 ||
            validatedOidcToken.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException(
                "Требуется однозначный, проверенный OIDC ID token.");

        Claim[] subjects = validatedOidcToken.FindAll("sub").ToArray();
        Claim[] userIds = validatedOidcToken.FindAll(Gup2UserIdClaim).ToArray();
        if (subjects.Length != 1 ||
            string.IsNullOrWhiteSpace(subjects[0].Value) ||
            subjects[0].Value.Length > 256 ||
            userIds.Length != 1 ||
            !CanonicalGup2Uuid(userIds[0].Value) ||
            validatedOidcToken.HasClaim(c => c.Type == ClaimTypes.NameIdentifier))
            throw new UnauthorizedAccessException(
                "OIDC ID token не связывает пользователя с единственным UUID GUP-2.");

        // The ASP.NET signed/verified authentication ticket contains ONLY the
        // mapped UUID; transient access, refresh and ID tokens are not stored.
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userIds[0].Value)], Scheme);
        return new ClaimsPrincipal(identity);
    }

    private static bool CanonicalGup2Uuid(string? value) =>
        value is { Length: 36 } &&
        Guid.TryParseExact(value, "D", out Guid parsed) &&
        parsed != Guid.Empty &&
        parsed.ToString("D") == value;
}
