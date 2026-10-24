using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GetDocument.WopiGateway;

/// <summary>
/// The server must enforce AD/user ACL, stable document ownership and an immutable
/// revision snapshot on EACH Resolve call. This interface never accepts raw SMB paths.
/// A service account alone MUST NOT imply that an end-user is allowed to read a file.
/// </summary>
public interface ITrustedWopiDocumentStore
{
    ValueTask<WopiAuthorizedContent?> ResolveForReadAsync(
        string documentId, string revisionId, string userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The source returned by a trusted store must be a version-pinned, immutable,
/// seekable byte snapshot, not a mutable shared original open for concurrent writes.
/// </summary>
public sealed record WopiAuthorizedContent(
    WopiDocument Metadata,
    string ExpectedSha256,
    Func<CancellationToken, ValueTask<Stream>> OpenReadAsync);

/// <summary>Safe fallback: absent enterprise identity/ACL integration, serve no files.</summary>
public sealed class DenyAllWopiDocumentStore : ITrustedWopiDocumentStore
{
    public ValueTask<WopiAuthorizedContent?> ResolveForReadAsync(
        string documentId, string revisionId, string userId, CancellationToken cancellationToken)
        => ValueTask.FromResult<WopiAuthorizedContent?>(null);
}

/// <summary>
/// Created only in the server with secrets and pinned public proof keys loaded
/// from trusted configuration / an authenticated Collabora discovery channel.
/// </summary>
public sealed class WopiGatewaySecurity
{
    internal byte[] SigningSecret { get; }
    public Uri PublicOrigin { get; }
    public RSAParameters CurrentProofKey { get; }
    public RSAParameters OldProofKey { get; }

    public WopiGatewaySecurity(byte[] signingSecret, Uri publicOrigin,
        RSAParameters currentProofKey, RSAParameters oldProofKey)
    {
        if (signingSecret is null || signingSecret.Length < 32)
            throw new ArgumentException("WOPI требует серверный HS256 секрет >=32 байт.");
        OnlyOfficeUriPolicy.RequireHttpsOrigin(publicOrigin);
        if (currentProofKey.Modulus is null || currentProofKey.Modulus.Length < 256 ||
            currentProofKey.Exponent is null || currentProofKey.Exponent.Length == 0 ||
            oldProofKey.Modulus is null || oldProofKey.Modulus.Length < 256 ||
            oldProofKey.Exponent is null || oldProofKey.Exponent.Length == 0)
            throw new ArgumentException("Требуются проверенные RSA proof keys >=2048 бит.");
        SigningSecret = (byte[])signingSecret.Clone();
        PublicOrigin = publicOrigin;
        CurrentProofKey = currentProofKey;
        OldProofKey = oldProofKey;
    }
}

/// <summary>
/// Read-only WOPI HTTP API. SECURITY: invalid proof/token, wrong document,
/// revoked ACL or missing provider must fail closed. No client-provided
/// filesystem paths, no PutFile/locks, no default filesystem scan.
/// </summary>
public static class WopiHttpGateway
{
    private const long MaximumBytes = 32L * 1024 * 1024;

    public static IEndpointRouteBuilder MapReadOnlyWopi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/wopi/files/{fileId}", (
            HttpContext context, string fileId, ITrustedWopiDocumentStore store,
            WopiGatewaySecurity settings, TimeProvider clock) =>
            HandleCheckFileInfoAsync(context, fileId, store, settings, clock));
        routes.MapGet("/wopi/files/{fileId}/contents", (
            HttpContext context, string fileId, ITrustedWopiDocumentStore store,
            WopiGatewaySecurity settings, TimeProvider clock) =>
            HandleGetFileAsync(context, fileId, store, settings, clock));
        routes.MapPost("/wopi/files/{fileId}/contents", (HttpContext ctx) =>
        {
            ctx.Response.Headers["Allow"] = "GET";
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        });
        routes.MapPost("/wopi/files/{fileId}", (HttpContext ctx) =>
        {
            ctx.Response.Headers["Allow"] = "GET";
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        });
        return routes;
    }

    public static async Task HandleCheckFileInfoAsync(
        HttpContext context, string fileId, ITrustedWopiDocumentStore store,
        WopiGatewaySecurity settings, TimeProvider clock)
    {
        var access = await AuthorizeAsync(context, fileId, store, settings, clock);
        if (access.Status != 200)
        {
            context.Response.StatusCode = access.Status;
            return;
        }
        string json = WopiReadOnlySessionService.CheckFileInfo(
            access.Source!.Metadata, access.Grant!, clock.GetUtcNow());
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(json, context.RequestAborted);
    }

    public static async Task HandleGetFileAsync(
        HttpContext context, string fileId, ITrustedWopiDocumentStore store,
        WopiGatewaySecurity settings, TimeProvider clock)
    {
        var access = await AuthorizeAsync(context, fileId, store, settings, clock);
        if (access.Status != 200)
        {
            context.Response.StatusCode = access.Status;
            return;
        }
        var source = access.Source!;
        var plan = WopiReadOnlySessionService.PlanGetFile(source.Metadata,
            access.Grant!, clock.GetUtcNow());

        string? maxSizeHeader = context.Request.Headers["X-WOPI-MaxExpectedSize"];
        if (!string.IsNullOrEmpty(maxSizeHeader))
        {
            if (!long.TryParse(maxSizeHeader, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long maxSize) ||
                maxSize <= 0 || maxSize > int.MaxValue)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (plan.ExpectedSize > maxSize)
            {
                context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                return;
            }
        }

        if (plan.ExpectedSize is < 1 or > MaximumBytes)
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }
        if (source.ExpectedSha256.Length != 64 ||
            !source.ExpectedSha256.All(Uri.IsHexDigit))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        // A bounded snapshot is validated in full before the first byte
        // enters the HTTP response. Live SMB streams cannot be trusted to
        // remain unchanged during a response copy.
        byte[] content;
        try
        {
            await using var input = await source.OpenReadAsync(context.RequestAborted);
            if (!input.CanRead || !input.CanSeek || input.Length != plan.ExpectedSize)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
            input.Position = 0;
            content = new byte[checked((int)plan.ExpectedSize)];
            int filled = 0;
            while (filled < content.Length)
            {
                int count = await input.ReadAsync(
                    content.AsMemory(filled), context.RequestAborted);
                if (count == 0)
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    return;
                }
                filled += count;
            }
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)),
                    source.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   NotSupportedException or ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // All content is already checked and local to this bounded buffer.
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-WOPI-ItemVersion"] = source.Metadata.RevisionId;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = content.Length;
        await context.Response.Body.WriteAsync(content, context.RequestAborted);
    }

    private static async ValueTask<(WopiAuthorizedContent? Source, WopiReadOnlyGrant? Grant, int Status)>
        AuthorizeAsync(HttpContext ctx, string fileId, ITrustedWopiDocumentStore store,
            WopiGatewaySecurity settings, TimeProvider clock)
    {
        string? token = ctx.Request.Query["access_token"];
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16_384)
            return (null, null, StatusCodes.Status401Unauthorized);

        if (!long.TryParse(ctx.Request.Headers["X-WOPI-TimeStamp"],
                NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp))
            return (null, null, StatusCodes.Status403Forbidden);

        string publicUrl = ctx.Request.Scheme + "://" + ctx.Request.Host +
            ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString;
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var url) ||
            !WopiProofValidator.Verify(token, url, timestamp,
                ctx.Request.Headers["X-WOPI-Proof"].ToString(),
                ctx.Request.Headers["X-WOPI-ProofOld"].ToString(),
                settings.CurrentProofKey, settings.OldProofKey,
                settings.PublicOrigin, clock.GetUtcNow()))
            return (null, null, StatusCodes.Status403Forbidden);

        try
        {
            // Verify BEFORE resolving a document to avoid unauthorized metadata lookups.
            JsonElement claims = Hs256Jwt.Validate(token, settings.SigningSecret);
            string? doc = claims.GetProperty("doc").GetString();
            string? rev = claims.GetProperty("ver").GetString();
            string? user = claims.GetProperty("sub").GetString();
            long nbf = claims.GetProperty("nbf").GetInt64();
            long exp = claims.GetProperty("exp").GetInt64();
            long now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (doc != fileId || string.IsNullOrWhiteSpace(rev) ||
                string.IsNullOrWhiteSpace(user) ||
                claims.GetProperty("aud").GetString() != "get-document-wopi" ||
                claims.GetProperty("mode").GetString() != "view" ||
                nbf > now || exp <= now || exp - nbf is < 60 or > 900)
                return (null, null, StatusCodes.Status401Unauthorized);

            WopiAuthorizedContent? content = await store.ResolveForReadAsync(
                doc, rev, user, ctx.RequestAborted);
            if (content is null)
                return (null, null, StatusCodes.Status404NotFound);
            WopiReadOnlyGrant grant = WopiReadOnlySessionService.Authorize(
                token, settings.SigningSecret, content.Metadata, clock.GetUtcNow());
            return (content, grant, StatusCodes.Status200OK);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or JsonException or
                                  KeyNotFoundException or InvalidOperationException or
                                  FormatException or ArgumentException)
        {
            return (null, null, StatusCodes.Status401Unauthorized);
        }
    }
}
