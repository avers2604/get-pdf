using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GetDocument.WopiGateway;

/// <summary>
/// A trusted server-side ACL and revision adapter. It must check permissions
/// on EVERY request, including revocation and document/WORM classification.
/// A service-account filesystem permission is never an employee permission.
/// </summary>
public interface ITrustedRealtimeWopiDocumentStore
{
    ValueTask<WopiRealtimeAuthorizedContent?> ResolveForSessionAsync(
        string documentId, string sessionRevisionId, string userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// MUST use atomic expected-revision CAS and persist new immutable bytes,
    /// never overwrite an existing source or WORM original.
    /// </summary>
    ValueTask<StoredOfficeRevision> CommitNewRevisionAsync(
        WopiRealtimeAuthorizedContent authorized,
        RealtimeCoauthoringGrant grant, string? clientTimestamp,
        Stream fullOfficeDocument, CancellationToken cancellationToken);
}

public sealed record WopiRealtimeAuthorizedContent(
    WopiDocument Document,
    StoredOfficeRevision CurrentRevision,
    Func<CancellationToken, ValueTask<Stream>> OpenReadAsync);

/// <summary>
/// Default runtime: no directory, no identity provider, no document access.
/// Registration of a real provider requires explicit enterprise configuration.
/// </summary>
public sealed class DenyAllRealtimeWopiDocumentStore : ITrustedRealtimeWopiDocumentStore
{
    public ValueTask<WopiRealtimeAuthorizedContent?> ResolveForSessionAsync(
        string documentId, string sessionRevisionId, string userId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<WopiRealtimeAuthorizedContent?>(null);

    public ValueTask<StoredOfficeRevision> CommitNewRevisionAsync(
        WopiRealtimeAuthorizedContent authorized,
        RealtimeCoauthoringGrant grant, string? clientTimestamp,
        Stream fullOfficeDocument, CancellationToken cancellationToken) =>
        throw new UnauthorizedAccessException("Доверенный адаптер сохранения ещё не подключён.");
}

/// <summary>
/// WOPI HTTP interface for a Collabora realtime coauthoring session.
/// Synchronization/cursors belong to Collabora, not this HTTP host.
/// This route collection and MapReadOnlyWopi MUST NOT both be mapped on
/// the same URL space. No routes are enabled in this class automatically.
/// </summary>
public static class WopiLiveHttpGateway
{
    private const int MaximumBytes = 32 * 1024 * 1024;

    public static IEndpointRouteBuilder MapRealtimeWopi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/wopi/files/{fileId}", (
            HttpContext context, string fileId,
            ITrustedRealtimeWopiDocumentStore store,
            WopiGatewaySecurity settings, TimeProvider clock) =>
            HandleCheckFileInfoAsync(context, fileId, store, settings, clock));
        routes.MapGet("/wopi/files/{fileId}/contents", (
            HttpContext context, string fileId,
            ITrustedRealtimeWopiDocumentStore store,
            WopiGatewaySecurity settings, TimeProvider clock) =>
            HandleGetFileAsync(context, fileId, store, settings, clock));
        routes.MapPost("/wopi/files/{fileId}/contents", (
            HttpContext context, string fileId,
            ITrustedRealtimeWopiDocumentStore store,
            WopiGatewaySecurity settings, TimeProvider clock) =>
            HandlePutFileAsync(context, fileId, store, settings, clock));
        routes.MapPost("/wopi/files/{fileId}", (HttpContext context) =>
        {
            context.Response.Headers["Allow"] = "GET";
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        });
        return routes;
    }

    public static async Task HandleCheckFileInfoAsync(
        HttpContext context, string fileId, ITrustedRealtimeWopiDocumentStore store,
        WopiGatewaySecurity settings, TimeProvider clock)
    {
        var auth = await AuthorizeAsync(context, fileId, store, settings, clock);
        if (auth.Status != StatusCodes.Status200OK)
        {
            context.Response.StatusCode = auth.Status;
            return;
        }
        try
        {
            string json = RealtimeCoauthoring.CheckFileInfo(
                auth.Source!.Document, auth.Grant!, clock.GetUtcNow(),
                auth.Source.CurrentRevision);
            SetNoStore(context);
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(json, context.RequestAborted);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    public static async Task HandleGetFileAsync(
        HttpContext context, string fileId, ITrustedRealtimeWopiDocumentStore store,
        WopiGatewaySecurity settings, TimeProvider clock)
    {
        var auth = await AuthorizeAsync(context, fileId, store, settings, clock);
        if (auth.Status != StatusCodes.Status200OK)
        {
            context.Response.StatusCode = auth.Status;
            return;
        }
        var source = auth.Source!;
        var rev = source.CurrentRevision;
        if (rev.Length is <= 0 or > MaximumBytes ||
            rev.Sha256 is null || rev.Sha256.Length != 64 ||
            !rev.Sha256.All(Uri.IsHexDigit))
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }
        if (context.Request.Headers.TryGetValue("X-WOPI-MaxExpectedSize", out var header))
        {
            if (!long.TryParse(header.ToString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out long maxExpected) || maxExpected <= 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (rev.Length > maxExpected)
            {
                context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                return;
            }
        }

        byte[] bytes = new byte[checked((int)rev.Length)];
        try
        {
            await using var input = await source.OpenReadAsync(context.RequestAborted);
            if (!input.CanRead || !input.CanSeek || input.Length != rev.Length)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
            input.Position = 0;
            int total = 0;
            while (total < bytes.Length)
            {
                int count = await input.ReadAsync(
                    bytes.AsMemory(total), context.RequestAborted);
                if (count == 0)
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    return;
                }
                total += count;
            }
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
                    rev.Sha256, StringComparison.OrdinalIgnoreCase))
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                   UnauthorizedAccessException or NotSupportedException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        SetNoStore(context);
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers["X-WOPI-ItemVersion"] = rev.RevisionId;
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    public static async Task HandlePutFileAsync(
        HttpContext context, string fileId, ITrustedRealtimeWopiDocumentStore store,
        WopiGatewaySecurity settings, TimeProvider clock)
    {
        var auth = await AuthorizeAsync(context, fileId, store, settings, clock);
        if (auth.Status != StatusCodes.Status200OK)
        {
            context.Response.StatusCode = auth.Status;
            return;
        }
        var source = auth.Source!;
        var grant = auth.Grant!;

        if (!grant.CanEdit || !source.Document.CanEdit || source.Document.IsWormOriginal)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        // Collabora's WOPI-like PutFile can omit the MS-WOPI override
        // header. A nonempty operation must still explicitly be PUT.
        string overrideOperation = context.Request.Headers["X-WOPI-Override"].ToString();
        if (overrideOperation.Length != 0 &&
            !string.Equals(overrideOperation, "PUT", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var timestamp = context.Request.Headers["X-COOL-WOPI-Timestamp"].ToString();
        try
        {
            // Check early, then MUST check again inside the store under CAS.
            RealtimeCoauthoring.PlanNewRevision(
                source.Document, grant, clock.GetUtcNow(),
                source.CurrentRevision.LastModifiedUtc, timestamp);
        }
        catch (WopiVersionConflictException)
        {
            await WriteConflictAsync(context);
            return;
        }
        if (context.Request.ContentLength > MaximumBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (context.Request.ContentLength is 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // A bounded stream also supports HTTP chunked transfer without
        // Content-Length. Never trust a declared size without counting bytes.
        try
        {
            await using var verifiedPayload = new MemoryStream();
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                int read = await context.Request.Body.ReadAsync(
                    buffer.AsMemory(), context.RequestAborted);
                if (read == 0) break;
                if (verifiedPayload.Length + read > MaximumBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
                await verifiedPayload.WriteAsync(
                    buffer.AsMemory(0, read), context.RequestAborted);
            }
            if (verifiedPayload.Length == 0 ||
                (context.Request.ContentLength is { } declared &&
                 verifiedPayload.Length != declared))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            verifiedPayload.Position = 0;
            StoredOfficeRevision saved = await store.CommitNewRevisionAsync(
                source, grant, timestamp, verifiedPayload, context.RequestAborted);
            if (saved.DocumentId != fileId || saved.RevisionId == source.CurrentRevision.RevisionId)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            SetNoStore(context);
            context.Response.Headers["X-WOPI-ItemVersion"] = saved.RevisionId;
            context.Response.ContentType = "application/json; charset=utf-8";
            string json = JsonSerializer.Serialize(new
            {
                LastModifiedTime = saved.LastModifiedUtc.ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture)
            });
            await context.Response.WriteAsync(json, context.RequestAborted);
        }
        catch (WopiVersionConflictException)
        {
            await WriteConflictAsync(context);
        }
        catch (OfficeRevisionConflictException)
        {
            await WriteConflictAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                   NotSupportedException or ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    private static async Task WriteConflictAsync(HttpContext context)
    {
        SetNoStore(context);
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
            """{"COOLStatusCode":1010}""", context.RequestAborted);
    }

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static async ValueTask<(WopiRealtimeAuthorizedContent? Source,
        RealtimeCoauthoringGrant? Grant, int Status)> AuthorizeAsync(
        HttpContext context, string fileId, ITrustedRealtimeWopiDocumentStore store,
        WopiGatewaySecurity settings, TimeProvider clock)
    {
        var query = context.Request.Query["access_token"];
        if (query.Count != 1 || string.IsNullOrWhiteSpace(query[0]) ||
            query[0]!.Length > 16_384)
            return (null, null, StatusCodes.Status401Unauthorized);
        string token = query[0]!;

        if (!long.TryParse(context.Request.Headers["X-WOPI-TimeStamp"],
                NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp))
            return (null, null, StatusCodes.Status403Forbidden);

        string requestUrl = context.Request.Scheme + "://" + context.Request.Host +
            context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var actualUrl) ||
            !WopiProofValidator.Verify(token, actualUrl, timestamp,
                context.Request.Headers["X-WOPI-Proof"].ToString(),
                context.Request.Headers["X-WOPI-ProofOld"].ToString(),
                settings.CurrentProofKey, settings.OldProofKey,
                settings.PublicOrigin, clock.GetUtcNow()))
            return (null, null, StatusCodes.Status403Forbidden);

        try
        {
            // Signed JWT checks happen BEFORE any enterprise ACL lookup.
            JsonElement jwt = Hs256Jwt.Validate(token, settings.SigningSecret);
            string? doc = jwt.GetProperty("doc").GetString();
            string? rev = jwt.GetProperty("ver").GetString();
            string? user = jwt.GetProperty("sub").GetString();
            string? mode = jwt.GetProperty("mode").GetString();
            long nbf = jwt.GetProperty("nbf").GetInt64();
            long exp = jwt.GetProperty("exp").GetInt64();
            long unixNow = clock.GetUtcNow().ToUnixTimeSeconds();
            if (doc != fileId || string.IsNullOrWhiteSpace(rev) ||
                string.IsNullOrWhiteSpace(user) ||
                jwt.GetProperty("aud").GetString() != "get-document-wopi-live" ||
                (mode != "edit" && mode != "view") ||
                nbf > unixNow || exp <= unixNow || exp - nbf is < 60 or > 36000)
                return (null, null, StatusCodes.Status401Unauthorized);

            WopiRealtimeAuthorizedContent? content = await store.ResolveForSessionAsync(
                doc, rev, user, context.RequestAborted);
            if (content is null)
                return (null, null, StatusCodes.Status404NotFound);
            RealtimeCoauthoringGrant grant = RealtimeCoauthoring.Authorize(
                token, settings.SigningSecret, content.Document, clock.GetUtcNow());
            if (content.CurrentRevision.DocumentId != content.Document.DocumentId)
                return (null, null, StatusCodes.Status409Conflict);
            return (content, grant, StatusCodes.Status200OK);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or JsonException or
                                   KeyNotFoundException or InvalidOperationException or
                                   FormatException or ArgumentException)
        {
            return (null, null, StatusCodes.Status401Unauthorized);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return (null, null, StatusCodes.Status503ServiceUnavailable);
        }
    }
}
