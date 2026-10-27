using GetDocument.Collaboration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// GD-25.3: corporate SSO MUST originate in an independently validated OIDC
// JWT from the pinned internal IdP, not an X-Forwarded-User header or a
// manually created ClaimsPrincipal. Off by default.
bool corporateSsoEnabled = EuroOfficeCorporateSsoPolicy.IsSsoEnabled(
    Environment.GetEnvironmentVariable("GET_EUROOFFICE_SSO_ENABLED"));
if (corporateSsoEnabled)
{
    var oidc = EuroOfficeCorporateSsoPolicy.CreateSettings(
        Environment.GetEnvironmentVariable("GET_EUROOFFICE_SSO_AUTHORITY"),
        Environment.GetEnvironmentVariable("GET_EUROOFFICE_SSO_ISSUER"),
        Environment.GetEnvironmentVariable("GET_EUROOFFICE_SSO_AUDIENCE"));

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = oidc.Authority;
            options.Audience = oidc.Audience;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.SaveToken = false;
            options.BackchannelTimeout = TimeSpan.FromSeconds(10);
            options.RefreshOnIssuerKeyNotFound = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = oidc.Issuer,
                ValidateAudience = true,
                ValidAudience = oidc.Audience,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    try
                    {
                        EuroOfficeCorporateSsoPolicy.BindVerifiedEmployee(
                            context.Principal, TimeProvider.System.GetUtcNow());
                    }
                    catch (UnauthorizedAccessException)
                    {
                        context.Fail("Invalid immutable GUP-2 employee identity.");
                    }
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization(options =>
        options.AddPolicy("EuroOfficeCorporateSso", policy =>
            policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()));
}

// Do not log request-start paths containing one-time launch handles.
// Reverse proxies must redact /get-document/editor/launch/{nonce} as well.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

// Corporate identities and single-use grants are intentionally NOT faked.
// Register enterprise authentication and replace DenyAllEuroOfficeLaunchStore
// only after the GUP-2/AD policy, revocation and immutable revision sink exist.
builder.Services.AddSingleton<ITrustedEuroOfficeLaunchStore, DenyAllEuroOfficeLaunchStore>();
builder.Services.AddSingleton<ITrustedEuroOfficeCallbackSessionStore,
    DenyAllEuroOfficeCallbackSessionStore>();
builder.Services.AddSingleton<ITrustedEuroOfficeCallbackRevisionSink,
    DenyAllEuroOfficeCallbackRevisionSink>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

// Mandatory explicit pinned HTTPS endpoint and server-side JWT secret.
// No default secrets, generated demo sessions or anonymous document loading.
string RequireEnvironment(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException("Отсутствует обязательный параметр: " + name);

if (!Uri.TryCreate(RequireEnvironment("GET_EUROOFFICE_ORIGIN"),
        UriKind.Absolute, out Uri? officeOrigin))
    throw new InvalidOperationException("Некорректный GET_EUROOFFICE_ORIGIN.");
OnlyOfficeUriPolicy.RequireHttpsOrigin(officeOrigin);

byte[] signingKey;
try
{
    signingKey = Convert.FromBase64String(RequireEnvironment("GET_EUROOFFICE_JWT_KEY_B64"));
}
catch (FormatException ex)
{
    throw new InvalidOperationException("Некорректный ключ Euro-Office (base64).", ex);
}
if (signingKey.Length < 32)
    throw new InvalidOperationException("Ключ Euro-Office должен содержать минимум 32 байта.");
// Incoming editor config JWT and outgoing callback JWT may use different
// Document Server secrets. Require an independent pinned outbox secret before
// the callback route can ever be enabled.
bool callbacksEnabled =
    Environment.GetEnvironmentVariable("GET_EUROOFFICE_CALLBACKS_ENABLED") == "1";
byte[]? outboxSigningKey = null;
if (callbacksEnabled)
{
    try
    {
        outboxSigningKey = Convert.FromBase64String(
            RequireEnvironment("GET_EUROOFFICE_OUTBOX_JWT_KEY_B64"));
    }
    catch (FormatException ex)
    {
        throw new InvalidOperationException("Неверный outbox JWT-ключ Euro-Office.", ex);
    }
    if (outboxSigningKey.Length < 32)
        throw new InvalidOperationException("Euro-Office outbox JWT ключ слишком короткий.");
}
builder.Services.AddSingleton(new EuroOfficeHostSecrets(
    officeOrigin, signingKey, outboxSigningKey));

var app = builder.Build();

// Never emit a stack trace or editor bootstrap in an HTTP error response.
// Reverse proxy and enterprise access logs must redact auth headers.
app.UseExceptionHandler(handler => handler.Run(context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "text/plain; charset=utf-8";
    return context.Response.WriteAsync("Внутренняя ошибка запуска редактора.");
}));

app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    if (!context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

// Authenticate JWT before resolving any corporate editor launch. The
// incoming callback has its independent server-to-server outbox signature
// and is NOT granted access by a user's SSO bearer token.
if (corporateSsoEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Liveness does not imply that real company files are accessible.
app.MapGet("/health/live", () => Results.Ok(new { status = "live", authorizedStorage = false }));

var launchRoute = app.MapGet("/get-document/editor/launch/{launchId}", async (
    string launchId, HttpContext context, ITrustedEuroOfficeLaunchStore store,
    EuroOfficeHostSecrets secrets, TimeProvider clock) =>
{
    // The NativeWebView must be authenticated by the corporate SSO broker.
    // A matching path is not itself proof of permission to read the document.
    if (!corporateSsoEnabled)
        return Results.NotFound();
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    if (!EuroOfficeLaunchId.IsValid(launchId))
        return Results.NotFound();

    // MUST be an atomic, single-use lookup tied to this exact identity.
    // The shipped store is DenyAll, so this never returns a real document.
    try
    {
        var launch = await EuroOfficeLaunchResolver.ResolveAsync(
            launchId, context.User, store, secrets.OfficeOrigin, secrets.SigningKey,
            clock.GetUtcNow(), context.RequestAborted);
        if (launch is null)
            return Results.NotFound();
        context.Response.Headers["Content-Security-Policy"] = launch.ContentSecurityPolicy;
        return Results.Content(launch.Html, "text/html; charset=utf-8");
    }
    catch (UnauthorizedAccessException)
    {
        return Results.NotFound();
    }
    catch (ArgumentException)
    {
        // Invalid/mismatched trusted backend values must never cause an
        // automatic fallback to an unpinned Euro-Office server.
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
});
// Even a valid SSO user does not bypass the still-denying grant provider.
if (corporateSsoEnabled)
    launchRoute.RequireAuthorization("EuroOfficeCorporateSso");


// Native Docs API callback is disabled independently of editor launch.
// The default session store and revision sink both deny all corporate files.
app.MapPost("/get-document/editor/callback/{callbackId}", async (
    string callbackId, HttpContext context,
    ITrustedEuroOfficeCallbackSessionStore sessions,
    ITrustedEuroOfficeCallbackRevisionSink sink,
    EuroOfficeHostSecrets secrets, TimeProvider clock) =>
{
    if (!callbacksEnabled || !EuroOfficeLaunchId.IsValid(callbackId))
        return Results.NotFound();
    if (context.Request.Cookies.Count > 0 ||
        context.Request.Headers.ContainsKey("Origin") ||
        !string.Equals(
            context.Request.ContentType?.Split(';', 2)[0].Trim(),
            "application/json", StringComparison.OrdinalIgnoreCase) ||
        context.Request.ContentLength is null or <= 0 or > 65536)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    // The session must come from trusted corporate storage, NOT a client
    // supplied document key or unverified JWT user field.
    var session = await sessions.FindAsync(callbackId, context.RequestAborted);
    if (session is null)
        return Results.NotFound();

    try
    {
        EuroOfficeCallbackAdmission.RequireCurrent(
            session, secrets.OfficeOrigin, clock.GetUtcNow());

        // Only an explicitly configured outbound JWT header is accepted.
        // Default matches the internal Euro-Office server lab policy;
        // actual header format must be verified with the pinned release.
        string token = context.Request.Headers["AuthorizationJWT"].ToString();
        if (token.StartsWith("Bearer ", StringComparison.Ordinal))
            token = token["Bearer ".Length..];
        if (string.IsNullOrEmpty(token) || token.Length > 65536)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        using var reader = new StreamReader(
            context.Request.Body, System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: 8192,
            leaveOpen: true);
        string body = await reader.ReadToEndAsync(context.RequestAborted);
        EuroOfficeSaveCandidate? candidate = EuroOfficeNativeCallback.Plan(
            body, token, session.DocumentKey, session.TrustedDocumentServerOrigin,
            secrets.OutboxSigningKey!);

        // A correctly authenticated status 1/4 has no revision to save.
        if (candidate is null)
            return Results.Json(new { error = 0 });

        // A positive acknowledgment requires a real, approved GUP-2 CAS
        // commit. DenyAll returns false, so no changed document is lost
        // through a misleading HTTP 200 {"error":0}.
        EuroOfficeCallbackCommitReceipt receipt =
            await sink.CommitAsync(candidate, context.RequestAborted);
        int error = EuroOfficeCallbackAdmission.ResponseError(receipt);
        return error == 0
            ? Results.Json(new { error = 0 })
            : Results.Json(new { error = 1 },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or
        ArgumentException or InvalidDataException)
    {
        return Results.Json(new { error = 1 },
            statusCode: StatusCodes.Status403Forbidden);
    }
});

app.Run();

internal sealed record EuroOfficeHostSecrets(
    Uri OfficeOrigin, byte[] SigningKey, byte[]? OutboxSigningKey);
