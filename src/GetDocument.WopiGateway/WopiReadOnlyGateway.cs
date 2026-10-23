using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GetDocument.Collaboration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GetDocument.WopiGateway;

/// <summary>
/// Server-owned immutable revision descriptor. Its ID comes from a trusted
/// repository and never from an absolute UNC path supplied by the requester.
/// </summary>
public sealed record WopiStoredRevision(
    string DocumentId, string RevisionId, string FileName, string OwnerId,
    long Size, string Sha256, bool IsWormOriginal);

public sealed record WopiAuthorizedUser(string UserId, string DisplayName);

/// <summary>
/// This contract must be backed by the approved repository/SMB service.
/// OpenReadAsync MUST open the exact immutable revision returned by FindAsync.
/// It MUST NOT accept filesystem paths from WOPI input.
/// </summary>
public interface IWopiReadOnlyStore
{
    ValueTask<WopiStoredRevision?> FindAsync(
        string documentId, string revisionId, CancellationToken cancellationToken);

    ValueTask<Stream> OpenReadAsync(
        WopiStoredRevision revision, CancellationToken cancellationToken);
}

/// <summary>
/// A mandatory, server-side AD/ACL or GUP-2 policy check on EACH WOPI request.
/// Returning null is deny. A startup without this implementation fails closed.
/// </summary>
public interface IWopiReadAuthorization
{
    ValueTask<WopiAuthorizedUser?> CheckReadAsync(
        string userId, WopiStoredRevision revision, CancellationToken cancellationToken);
}

/// <summary>
/// Configure via trusted server secrets and pinned Collabora discovery public keys.
/// No signing secret, private RSA key, or filesystem path may enter Avalonia.
/// </summary>
public sealed class WopiReadOnlyGatewayOptions
{
    private readonly byte[] _secret;

    public Uri PublicOrigin { get; }
    public RSAParameters CurrentProofKey { get; }
    public RSAParameters PreviousProofKey { get; }
    internal byte[] Secret => _secret;

    public WopiReadOnlyGatewayOptions(
        Uri publicOrigin, byte[] serverJwtKey,
        RSAParameters currentProofKey, RSAParameters previousProofKey)
    {
        OnlyOfficeUriPolicy.RequireHttpsOrigin(publicOrigin);
        ArgumentNullException.ThrowIfNull(serverJwtKey);
        if (serverJwtKey.Length < 32)
            throw new ArgumentException("Нужен серверный JWT секрет от 32 байт.", nameof(serverJwtKey));
        if (currentProofKey.Modulus is null || currentProofKey.Exponent is null ||
            previousProofKey.Modulus is null || previousProofKey.Exponent is null)
            throw new ArgumentException("Публичные RSA-ключи должны приходить из доверенного WOPI discovery.");
        PublicOrigin = publicOrigin;
        _secret = serverJwtKey.ToArray();
        CurrentProofKey = currentProofKey;
        PreviousProofKey = previousProofKey;
    }
}

/// <summary>
/// A deliberately read-only WOPI host. Two GET endpoints only.
/// No PutFile, PutRelativeFile, locking, token-issuance, or raw SMB endpoint.
/// </summary>
public static class WopiReadOnlyGateway
{
    public const int MaxContentBytes = 32 * 1024 * 1024;

    public static IEndpointRouteBuilder MapGetDocumentWopiReadOnly(
        this IEndpointRouteBuilder endpoints, WopiReadOnlyGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        endpoints.MapGet("/wopi/files/{fileId}", (
            HttpContext context, string fileId,
            IWopiReadOnlyStore store, IWopiReadAuthorization authorization,
            CancellationToken cancellationToken) =>
            HandleAsync(context, fileId, false, options, store, authorization,
                DateTimeOffset.UtcNow, cancellationToken));
        endpoints.MapGet("/wopi/files/{fileId}/contents", (
            HttpContext context, string fileId,
            IWopiReadOnlyStore store, IWopiReadAuthorization authorization,
            CancellationToken cancellationToken) =>
            HandleAsync(context, fileId, true, options, store, authorization,
                DateTimeOffset.UtcNow, cancellationToken));
        return endpoints;
    }

    /// <summary>
    /// Public for host-independent tests. Request checks and content read are
    /// performed here, NOT in a client-side JavaScript editor.
    /// </summary>
    public static async Task<IResult> HandleAsync(
        HttpContext context, string fileId, bool contents,
        WopiReadOnlyGatewayOptions options, IWopiReadOnlyStore store,
        IWopiReadAuthorization authorization, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorization);

        context.Response.Headers.CacheControl = "private, no-store, max-age=0";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-WOPI-ServerVersion"] = "GET-Document-WOPI-readonly/0.1";

        if (!IsSafeFileId(fileId))
            return Results.NotFound();
        string expectedPath = "/wopi/files/" + fileId + (contents ? "/contents" : "");
        if (!string.Equals(context.Request.Path.Value, expectedPath, StringComparison.Ordinal) ||
            context.Request.PathBase.HasValue)
            return Results.NotFound();

        // This host refuses arbitrary Forwarded and X-Forwarded-* values.
        // TLS must be enforced by the trusted server before routing here.
        if (!string.Equals(context.Request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(context.Request.Host.Value, options.PublicOrigin.Authority,
                StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest();

        var values = context.Request.Query["access_token"];
        if (values.Count != 1 || values[0] is not { Length: > 0 and <= 16384 } token)
            return Results.Unauthorized();

        JsonElement payload;
        string userId;
        string revisionId;
        try
        {
            payload = Hs256Jwt.Validate(token, options.Secret);
            userId = payload.GetProperty("sub").GetString() ?? "";
            string id = payload.GetProperty("doc").GetString() ?? "";
            revisionId = payload.GetProperty("ver").GetString() ?? "";
            if (id != fileId || !IsSafeFileId(id) ||
                string.IsNullOrWhiteSpace(userId) || userId.Length > 200 ||
                string.IsNullOrWhiteSpace(revisionId) || revisionId.Length > 200)
                return Results.Unauthorized();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or KeyNotFoundException
                                   or InvalidOperationException or FormatException or JsonException)
        {
            return Results.Unauthorized();
        }

        var timestampHeader = context.Request.Headers["X-WOPI-TimeStamp"];
        var currentProof = context.Request.Headers["X-WOPI-Proof"];
        var oldProof = context.Request.Headers["X-WOPI-ProofOld"];
        if (timestampHeader.Count != 1 || currentProof.Count > 1 || oldProof.Count > 1 ||
            !long.TryParse(timestampHeader[0], NumberStyles.None, CultureInfo.InvariantCulture,
                out long ticks))
            return Results.StatusCode(StatusCodes.Status500InternalServerError);

        Uri externalUrl;
        try
        {
            externalUrl = new Uri(options.PublicOrigin.GetLeftPart(UriPartial.Authority) +
                context.Request.Path.ToUriComponent() +
                context.Request.QueryString.ToUriComponent(), UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        if (!WopiProofValidator.Verify(
                token, externalUrl, ticks,
                currentProof.Count == 1 ? currentProof[0] : null,
                oldProof.Count == 1 ? oldProof[0] : null,
                options.CurrentProofKey, options.PreviousProofKey, options.PublicOrigin, now))
            return Results.StatusCode(StatusCodes.Status500InternalServerError);

        WopiStoredRevision? revision;
        WopiAuthorizedUser? user;
        try
        {
            revision = await store.FindAsync(fileId, revisionId, cancellationToken);
            if (revision is null || revision.DocumentId != fileId ||
                revision.RevisionId != revisionId)
                return Results.NotFound();
            user = await authorization.CheckReadAsync(userId, revision, cancellationToken);
            if (user is null || user.UserId != userId)
                return Results.NotFound();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (IOException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
        if (revision.Size < 1 || revision.Size > MaxContentBytes ||
            revision.Sha256.Length != 64)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        context.Response.Headers["X-WOPI-ItemVersion"] = revision.RevisionId;
        var doc = new WopiDocument(
            revision.DocumentId, revision.RevisionId, revision.FileName,
            revision.OwnerId, user.UserId, user.DisplayName, revision.Size,
            CanEdit: false, IsWormOriginal: revision.IsWormOriginal);
        WopiReadOnlyGrant grant;
        try
        {
            grant = WopiReadOnlySessionService.Authorize(token, options.Secret, doc, now);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException)
        {
            return Results.Unauthorized();
        }

        if (!contents)
        {
            try
            {
                string json = WopiReadOnlySessionService.CheckFileInfo(doc, grant, now);
                return Results.Content(json, "application/json; charset=utf-8");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
            {
                return Results.NotFound();
            }
        }

        var clientLimit = context.Request.Headers["X-WOPI-MaxExpectedSize"];
        if (clientLimit.Count > 1 ||
            (clientLimit.Count == 1 &&
             (!long.TryParse(clientLimit[0], NumberStyles.None,
                 CultureInfo.InvariantCulture, out long limit) || limit < 0)))
            return Results.BadRequest();
        if (clientLimit.Count == 1 &&
            long.TryParse(clientLimit[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out long cap) && cap < revision.Size)
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);

        try
        {
            _ = WopiReadOnlySessionService.PlanGetFile(doc, grant, now);
            // Buffer a strictly bounded, integrity-checked snapshot. Never return a
            // live mutable SMB stream whose bytes could change after verification.
            await using var source = await store.OpenReadAsync(revision, cancellationToken);
            byte[] data = new byte[checked((int)revision.Size)];
            int read = 0;
            while (read < data.Length)
            {
                int received = await source.ReadAsync(data.AsMemory(read), cancellationToken);
                if (received == 0)
                    return Results.Conflict();
                read += received;
            }
            byte[] extra = new byte[1];
            if (await source.ReadAsync(extra, cancellationToken) != 0)
                return Results.Conflict();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)),
                    revision.Sha256, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict();
            return Results.File(data, contentType: "application/octet-stream",
                enableRangeProcessing: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException or ArgumentException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static bool IsSafeFileId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 128 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
}
