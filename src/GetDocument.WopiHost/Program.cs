using System.Security.Cryptography;
using GetDocument.WopiGateway;

var builder = WebApplication.CreateBuilder(args);
// Do not let ASP.NET request-start messages reveal WOPI bearer access_token
// query parameters in ordinary application logs. Reverse-proxy logs must
// independently redact access_token as well.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

// FAIL CLOSED: no self-provisioning secret, anonymous file share or ACL bypass.
// Secrets should be mounted into the server process by corporate secret management.
// Discovery keys must be obtained/pinned from the authenticated internal Collabora.
// Never deploy without separate HTTPS/TLS (Kestrel + enterprise CA) configuration.
static string Required(string key) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value :
        throw new InvalidOperationException("Отсутствует защищённый параметр запуска: " + key);

static byte[] RequiredBytes(string key)
{
    try { return Convert.FromBase64String(Required(key)); }
    catch (FormatException ex) { throw new InvalidOperationException("Неверный base64 для: " + key, ex); }
}

static RSAParameters PublicProofKey(string keyPrefix) => new()
{
    Modulus = RequiredBytes(keyPrefix + "_MODULUS_B64"),
    Exponent = RequiredBytes(keyPrefix + "_EXPONENT_B64")
};

if (!Uri.TryCreate(Required("GET_WOPI_PUBLIC_ORIGIN"), UriKind.Absolute, out Uri? origin))
    throw new InvalidOperationException("Неверный GET_WOPI_PUBLIC_ORIGIN.");

builder.Services.AddSingleton(new WopiGatewaySecurity(
    RequiredBytes("GET_WOPI_HS256_KEY_B64"), origin,
    PublicProofKey("GET_WOPI_PROOF_CURRENT"),
    PublicProofKey("GET_WOPI_PROOF_OLD")));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

// No actual user/AD permissions resolver is connected yet. This is intentional:
// HTTP routes are implemented but cannot serve any corporate document by default.
builder.Services.AddSingleton<ITrustedWopiDocumentStore, DenyAllWopiDocumentStore>();
// Fail-closed even if the operator selects live mode: until GUP-2/AD ACL
// is installed, NO real files can be read and NO edits can be saved.
builder.Services.AddSingleton<ITrustedRealtimeWopiDocumentStore, DenyAllRealtimeWopiDocumentStore>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
// Route sets overlap: do NOT map both. Default is read-only.
// Live mode stays locked until the enterprise GUP-2/AD ACL + revision backend
// replaces the deny-all registration above and passes an integration gate.
bool enterpriseBackendConnected = false;
var mode = WopiHostMode.Resolve(
    Environment.GetEnvironmentVariable("GET_WOPI_HTTP_MODE"), enterpriseBackendConnected);
if (mode == WopiServerMode.Realtime)
    app.MapRealtimeWopi();
else
    app.MapReadOnlyWopi();
app.Run();
