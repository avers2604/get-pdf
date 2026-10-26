using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GetDocument.Collaboration;

var builder = WebApplication.CreateBuilder(args);

// Corporate identities and single-use grants are intentionally NOT faked.
// Register enterprise authentication and replace DenyAllEuroOfficeLaunchStore
// only after the GUP-2/AD policy, revocation and immutable revision sink exist.
builder.Services.AddSingleton<ITrustedEuroOfficeLaunchStore, DenyAllEuroOfficeLaunchStore>();
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
    await next(context);
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

    if (launchId is null || !Regex.IsMatch(launchId, @"\A[0-9a-f]{32}\z",
            RegexOptions.CultureInvariant))
        return Results.NotFound();

    // MUST be an atomic, single-use lookup tied to this exact identity.
    // The shipped store is DenyAll, so this never returns a real document.
    var authorized = await store.ConsumeOnceAsync(
        launchId, context.User, context.RequestAborted);
    if (authorized is null)
        return Results.NotFound();

    try
    {
        var launch = EuroOfficeLaunchPage.Render(
            authorized, secrets.OfficeOrigin, secrets.SigningKey, clock.GetUtcNow());
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

app.Run();

internal sealed record EuroOfficeHostSecrets(Uri OfficeOrigin, byte[] SigningKey);
