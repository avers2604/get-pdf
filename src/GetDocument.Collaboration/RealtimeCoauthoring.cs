using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Server-side co-authoring ticket. Different employees working on the SAME
/// document/revision share DocumentKey but never share bearer access tokens.
/// The actual real-time document/operation synchronization is Collabora Online.
/// </summary>
public sealed record RealtimeCoauthoringTicket(
    string DocumentId, string RevisionId,
    string DocumentKey, string AccessToken, long AccessTokenTtl, bool CanEdit);

public sealed record RealtimeCoauthoringGrant(
    string DocumentId, string RevisionId, string UserId,
    string DocumentKey, bool CanEdit, long ExpiresAtUnixSeconds);

/// <summary>
/// This plan authorizes creation of a SEPARATE immutable revision only.
/// It is NOT a PutFile implementation and never overwrites the source.
/// The gateway must make the comparison and durable write one atomic operation.
/// </summary>
public sealed record WopiNewRevisionPlan(
    string DocumentId, string ParentRevisionId, string UserId,
    DateTimeOffset ExpectedLastModifiedUtc,
    bool OverwriteOriginal = false, bool RequiredImmutableRevisionSink = true);

public sealed class WopiVersionConflictException : IOException
{
    public int CoolStatusCode => 1010;

    public WopiVersionConflictException(string message) : base(message) { }
}

/// <summary>
/// Protocol decisions only. Each request must still pass a trusted WOPI
/// HTTP host's proof validation, user ACL lookup, document/revision ownership,
/// token revocation and storage CAS. Do not enable editing simply because
/// this signed ticket can be issued.
/// </summary>
public static class RealtimeCoauthoring
{
    private const string Audience = "get-document-wopi-live";
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(1);

    public static RealtimeCoauthoringTicket Issue(
        WopiDocument document, byte[] secret, DateTimeOffset now,
        TimeSpan lifetime, bool editingGatewayReady)
    {
        ValidateDocument(document);
        if (lifetime < MinLifetime || lifetime > MaxLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime),
                "Срок доступа к совместной сессии должен быть от 1 до 15 минут.");
        bool canEdit = document.CanEdit && !document.IsWormOriginal && editingGatewayReady;
        string documentKey = RoomKey(document.DocumentId, document.RevisionId);
        long issued = now.ToUnixTimeSeconds();
        long expires = now.Add(lifetime).ToUnixTimeSeconds();
        // The gateway must NOT expose the HMAC secret to Avalonia or a browser.
        string token = Hs256Jwt.Sign(new
        {
            aud = Audience,
            sub = document.UserId,
            doc = document.DocumentId,
            ver = document.RevisionId,
            room = documentKey,
            mode = canEdit ? "edit" : "view",
            nbf = issued,
            exp = expires
        }, secret);
        return new RealtimeCoauthoringTicket(
            document.DocumentId, document.RevisionId, documentKey, token,
            now.Add(lifetime).ToUnixTimeMilliseconds(), canEdit);
    }

    public static RealtimeCoauthoringGrant Authorize(
        string accessToken, byte[] secret, WopiDocument document, DateTimeOffset now)
    {
        ValidateDocument(document);
        JsonElement jwt = Hs256Jwt.Validate(accessToken, secret);
        try
        {
            string? user = jwt.GetProperty("sub").GetString();
            string? doc = jwt.GetProperty("doc").GetString();
            string? rev = jwt.GetProperty("ver").GetString();
            string? room = jwt.GetProperty("room").GetString();
            string? mode = jwt.GetProperty("mode").GetString();
            long nbf = jwt.GetProperty("nbf").GetInt64();
            long exp = jwt.GetProperty("exp").GetInt64();
            long nowSeconds = now.ToUnixTimeSeconds();
            bool edit = mode == "edit";
            if (jwt.GetProperty("aud").GetString() != Audience ||
                user != document.UserId || doc != document.DocumentId ||
                rev != document.RevisionId ||
                room != RoomKey(document.DocumentId, document.RevisionId) ||
                (mode != "edit" && mode != "view") ||
                nbf > nowSeconds || exp <= nowSeconds ||
                exp - nbf is < 60 or > 900 ||
                (edit && (!document.CanEdit || document.IsWormOriginal)))
                throw new UnauthorizedAccessException(
                    "Права участника или срок совместной сессии не подтверждены.");
            return new RealtimeCoauthoringGrant(doc!, rev!, user!, room!, edit, exp);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UnauthorizedAccessException("Некорректная структура токена совместной работы.", ex);
        }
    }

    public static string CheckFileInfo(
        WopiDocument document, RealtimeCoauthoringGrant grant,
        DateTimeOffset now, DateTimeOffset lastModifiedUtc)
    {
        ValidateGrant(document, grant, now);
        bool writable = grant.CanEdit && document.CanEdit && !document.IsWormOriginal;
        return JsonSerializer.Serialize(new
        {
            BaseFileName = document.FileName,
            OwnerId = document.OwnerId,
            Size = document.FileSize,
            Version = document.RevisionId,
            UserId = document.UserId,
            UserFriendlyName = document.UserFriendlyName,
            UserCanWrite = writable,
            ReadOnly = !writable,
            SupportsUpdate = writable,
            // Collabora's WOPI-like integration relies on LastModifiedTime,
            // not the Microsoft 365 web WOPI Lock/RefreshLock operations.
            SupportsLocks = false,
            UserCanNotWriteRelative = true,
            LastModifiedTime = lastModifiedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DisablePrint = true,
            DisableBrowserCachingOfUserContent = true
        });
    }

    public static string CheckFileInfo(
        WopiDocument document, RealtimeCoauthoringGrant grant,
        DateTimeOffset now, StoredOfficeRevision stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ValidateGrant(document, grant, now);
        if (stored.DocumentId != document.DocumentId ||
            stored.Extension != Path.GetExtension(document.FileName).ToLowerInvariant())
            throw new UnauthorizedAccessException("Хранимая редакция не принадлежит документу.");

        bool editable = grant.CanEdit && document.CanEdit && !document.IsWormOriginal;
        return JsonSerializer.Serialize(new
        {
            BaseFileName = document.FileName,
            OwnerId = document.OwnerId,
            Size = stored.Length,
            Version = stored.RevisionId,
            UserId = document.UserId,
            UserFriendlyName = document.UserFriendlyName,
            UserCanWrite = editable,
            ReadOnly = !editable,
            SupportsUpdate = editable,
            SupportsLocks = false,
            UserCanNotWriteRelative = true,
            LastModifiedTime = stored.LastModifiedUtc.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
            DisablePrint = true,
            DisableBrowserCachingOfUserContent = true
        });
    }

    public static WopiNewRevisionPlan PlanNewRevision(
        WopiDocument document, RealtimeCoauthoringGrant grant,
        DateTimeOffset now, DateTimeOffset currentLastModifiedUtc,
        string? clientLastModifiedTimestamp)
    {
        ValidateGrant(document, grant, now);
        if (!grant.CanEdit || !document.CanEdit || document.IsWormOriginal)
            throw new UnauthorizedAccessException("Нет права на сохранение совместной редакции.");
        // Collabora can omit X-COOL-WOPI-Timestamp when the user requests
        // an override. We intentionally refuse this path: never overwrite.
        if (string.IsNullOrWhiteSpace(clientLastModifiedTimestamp) ||
            !DateTimeOffset.TryParseExact(clientLastModifiedTimestamp, "O",
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var requested) ||
            requested.UtcTicks != currentLastModifiedUtc.UtcTicks)
            throw new WopiVersionConflictException(
                "Документ изменился; создайте отдельную редакцию, не перезаписывайте оригинал.");
        return new WopiNewRevisionPlan(document.DocumentId, document.RevisionId,
            grant.UserId, currentLastModifiedUtc.ToUniversalTime());
    }

    private static void ValidateGrant(WopiDocument document, RealtimeCoauthoringGrant grant, DateTimeOffset now)
    {
        ValidateDocument(document);
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.ExpiresAtUnixSeconds <= now.ToUnixTimeSeconds() ||
            grant.DocumentId != document.DocumentId ||
            grant.RevisionId != document.RevisionId ||
            grant.UserId != document.UserId ||
            grant.DocumentKey != RoomKey(document.DocumentId, document.RevisionId) ||
            (grant.CanEdit && (!document.CanEdit || document.IsWormOriginal)))
            throw new UnauthorizedAccessException("Участник больше не имеет доступа к документу.");
    }

    private static string RoomKey(string documentId, string revisionId)
    {
        // Boundaries avoid ambiguous concatenation collisions.
        var data = Encoding.UTF8.GetBytes(
            documentId.Length.ToString(CultureInfo.InvariantCulture) + ":" + documentId +
            revisionId.Length.ToString(CultureInfo.InvariantCulture) + ":" + revisionId);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private static void ValidateDocument(WopiDocument? document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (string? part in new[] { document.DocumentId, document.RevisionId,
            document.FileName, document.OwnerId, document.UserId, document.UserFriendlyName })
        {
            if (string.IsNullOrWhiteSpace(part) || part.Length > 200 ||
                part.Any(ch => char.IsControl(ch) || char.IsSurrogate(ch) ||
                    CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format))
                throw new ArgumentException("Недопустимые данные совместного документа.");
        }
        if (document.FileName.IndexOfAny(['/', '\\']) >= 0 ||
            Path.GetExtension(document.FileName).ToLowerInvariant() is not (".docx" or ".xlsx" or ".pptx"))
            throw new NotSupportedException("Совместные сессии поддерживают только DOCX/XLSX/PPTX.");
        if (document.FileSize < 0 || document.FileSize > 256L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(document.FileSize));
    }
}
