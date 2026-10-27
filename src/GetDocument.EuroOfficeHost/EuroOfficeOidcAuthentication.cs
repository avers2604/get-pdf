using System.Security.Claims;
using GetDocument.Collaboration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GetDocument.EuroOfficeHost;

/// <summary>
/// GD-25.4: real standards-based enterprise OIDC authentication, but NOT a
/// document authorization grant. All editor launches remain disabled until
/// GUP-2 session-store and callback authorization are connected.
///
/// This registration uses discovery/JWKS from the pinned issuer only.
/// Reverse-proxy HTTP identity headers are never used as authentication.
/// </summary>
public static class EuroOfficeOidcAuthentication
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    private static readonly TimeSpan ShortTicketTtl = TimeSpan.FromMinutes(5);

    public static IServiceCollection AddCorporateOidc(
        this IServiceCollection services, EuroOfficeOidcConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieScheme;
            options.DefaultSignInScheme = CookieScheme;
            options.DefaultChallengeScheme = OidcScheme;
        })
        .AddCookie(CookieScheme, options =>
        {
            options.Cookie.Name = "__Host-GetEuroOfficeSession";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = ShortTicketTtl;
            options.Cookie.MaxAge = ShortTicketTtl;
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = context =>
            {
                IReadOnlySet<string> allowed = new HashSet<string>(StringComparer.Ordinal)
                    { EuroOfficeOidcIdentityMapper.Scheme };
                if (!EuroOfficeEnterprisePrincipalPolicy.IsApproved(
                    context.Principal, allowed))
                    context.RejectPrincipal();
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect(OidcScheme, options =>
        {
            options.Authority = configuration.Issuer;
            options.MetadataAddress = configuration.MetadataAddress.AbsoluteUri;
            options.RequireHttpsMetadata = true;
            options.ClientId = configuration.ClientId;
            options.ClientSecret = configuration.ClientSecret;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.UseTokenLifetime = false;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.MapInboundClaims = false;
            options.SignInScheme = CookieScheme;
            options.CallbackPath = "/signin-eurooffice-oidc";
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.BackchannelTimeout = TimeSpan.FromSeconds(15);
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                UseCookies = false,
                UseDefaultCredentials = false,
                AllowAutoRedirect = false
            };
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = configuration.Issuer,
                ValidateAudience = true,
                ValidAudience = configuration.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            };
            // Only replace the identity AFTER the completed remote OIDC flow,
            // including nonce/state checks, and just before issuing the
            // short-lived local authentication ticket.
            options.Events.OnTicketReceived = context =>
            {
                try
                {
                    context.Principal = EuroOfficeOidcIdentityMapper
                        .MapAfterTokenValidation(context.Principal);
                }
                catch (UnauthorizedAccessException)
                {
                    context.Fail("SSO ticket missing required GUP-2 identity.");
                }
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                if (!Uri.TryCreate(context.ProtocolMessage.IssuerAddress,
                        UriKind.Absolute, out Uri? authorizationEndpoint) ||
                    !configuration.IsTrustedAuthorizationEndpoint(authorizationEndpoint))
                    throw new InvalidOperationException(
                        "SSO authorization endpoint must be on the configured issuer origin.");
                return Task.CompletedTask;
            };
            options.Events.OnAuthenticationFailed = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return context.Response.WriteAsync("Корпоративная авторизация не подтверждена.");
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return context.Response.WriteAsync("Не удалось подтвердить корпоративный SSO.");
            };
        });

        return services;
    }

    public static IResult ChallengeCorporateSso() =>
        Results.Challenge(new AuthenticationProperties
        {
            RedirectUri = "/get-document/auth/status"
        }, [OidcScheme]);

    public static IResult AuthenticationStatus(ClaimsPrincipal user)
    {
        IReadOnlySet<string> permitted = new HashSet<string>(StringComparer.Ordinal)
            { EuroOfficeOidcIdentityMapper.Scheme };
        return EuroOfficeEnterprisePrincipalPolicy.IsApproved(user, permitted)
            ? Results.Json(new { authenticated = true, editorAccess = false })
            : Results.Unauthorized();
    }
}
