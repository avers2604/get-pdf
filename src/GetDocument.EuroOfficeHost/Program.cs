using GetDocument.Collaboration;

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddSingleton(new EuroOfficeHostSecrets(officeOrigin, signingKey));

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

// Liveness does not imply that real company files are accessible.
app.MapGet("/health/live", () => Results.Ok(new { status = "live", authorizedStorage = false }));

app.MapGet("/get-document/editor/launch/{launchId}", async (
    string launchId, HttpContext context, ITrustedEuroOfficeLaunchStore store,
    EuroOfficeHostSecrets secrets, TimeProvider clock) =>
{
    // The NativeWebView must be authenticated by the corporate SSO broker.
    // A matching path is not itself proof of permission to read the document.
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


// Native Docs API callback is disabled independently of editor launch.
// The default session store and revision sink both deny all corporate files.
bool callbacksEnabled =
    Environment.GetEnvironmentVariable("GET_EUROOFFICE_CALLBACKS_ENABLED") == "1";
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
        context.Request.ContentType != "application/json" ||
        context.Request.ContentLength is not > 0 and <= 65536)
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
            secrets.SigningKey);

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

internal sealed record EuroOfficeHostSecrets(Uri OfficeOrigin, byte[] SigningKey);
