using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Stable server document identifier and metadata. Never pass a Windows UNC
/// path, user-entered local filename or WORM storage credential as an ID.
/// ACLs must be checked independently by the internal WOPI host on EACH request.
/// </summary>
public sealed record WopiDocument(
    string DocumentId, string RevisionId, string FileName, string OwnerId,
    string UserId, string UserFriendlyName, long FileSize,
    bool CanEdit, bool IsWormOriginal);

public sealed record WopiReadOnlySession(string AccessToken, long AccessTokenTtl);

/// <summary>
/// Issued only by a server-side, already authenticated and authorized gateway.
/// All grants are deliberately read-only until a trustworthy PutFile/lock/revision
/// sink is built and locally tested. Reuses the existing Hs256Jwt from GD-09.
/// </summary>
public sealed record WopiReadOnlyGrant(
    string DocumentId, string RevisionId, string UserId, long ExpiresAtUnixSeconds);

/// <summary>
/// Authorization DECISION for a trusted HTTP GetFile host. This object does not
/// read the storage, reveal a pathname, or bypass server-side ACL checks.
/// </summary>
public sealed record WopiGetFilePlan(
    string DocumentId, string RevisionId, long ExpectedSize, bool ReadOnly = true);

public static class WopiReadOnlySessionService
{
    public static WopiReadOnlySession Issue(
        WopiDocument document, byte[] serverSecret,
        DateTimeOffset now, TimeSpan lifetime)
    {
        ValidateDocument(document);
        if (lifetime < TimeSpan.FromMinutes(1) || lifetime > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(lifetime),
                "Допустимый срок WOPI-токена — 1–15 минут.");
        long issued = now.ToUnixTimeSeconds();
        long expires = now.Add(lifetime).ToUnixTimeSeconds();
        string token = Hs256Jwt.Sign(new
        {
            aud = "get-document-wopi",
            sub = document.UserId,
            doc = document.DocumentId,
            ver = document.RevisionId,
            mode = "view",
            nbf = issued,
            exp = expires
        }, serverSecret);
        // WOPI access_token_ttl uses Unix epoch MILLISECONDS.
        return new WopiReadOnlySession(token, now.Add(lifetime).ToUnixTimeMilliseconds());
    }

    public static WopiReadOnlyGrant Authorize(
        string accessToken, byte[] serverSecret, WopiDocument expected,
        DateTimeOffset now)
    {
        ValidateDocument(expected);
        JsonElement verified = Hs256Jwt.Validate(accessToken, serverSecret);
        try
        {
            string? aud = verified.GetProperty("aud").GetString();
            string? sub = verified.GetProperty("sub").GetString();
            string? doc = verified.GetProperty("doc").GetString();
            string? version = verified.GetProperty("ver").GetString();
            string? mode = verified.GetProperty("mode").GetString();
            long nbf = verified.GetProperty("nbf").GetInt64();
            long exp = verified.GetProperty("exp").GetInt64();
            long nowSeconds = now.ToUnixTimeSeconds();

            if (aud != "get-document-wopi" || mode != "view" ||
                sub != expected.UserId || doc != expected.DocumentId ||
                version != expected.RevisionId || nbf > nowSeconds ||
                exp <= nowSeconds || exp - nbf is < 60 or > 900)
                throw new UnauthorizedAccessException(
                    "Токен WOPI истёк, не соответствует редакции или запрещён.");
            return new WopiReadOnlyGrant(doc!, version!, sub!, exp);
        }
        catch (Exception ex) when (
            ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UnauthorizedAccessException("Неверный формат WOPI-токена.", ex);
        }
    }

    public static string CheckFileInfo(WopiDocument doc, WopiReadOnlyGrant grant)
    {
        ValidateDocument(doc);
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.DocumentId != doc.DocumentId ||
            grant.RevisionId != doc.RevisionId || grant.UserId != doc.UserId)
            throw new UnauthorizedAccessException("Грант не относится к выбранному документу.");
        // JSON PascalCase keys follow MS-WOPI CheckFileInfo.
        // Do not claim ability to update/lock until the gateway implements it.
        return JsonSerializer.Serialize(new
        {
            BaseFileName = doc.FileName,
            OwnerId = doc.OwnerId,
            Size = doc.FileSize,
            Version = doc.RevisionId,
            UserId = doc.UserId,
            UserFriendlyName = doc.UserFriendlyName,
            UserCanWrite = false,
            ReadOnly = true,
            SupportsUpdate = false,
            SupportsLocks = false,
            DisablePrint = true,
            DisableBrowserCachingOfUserContent = true
        });
    }

    public static WopiGetFilePlan PlanGetFile(WopiDocument doc, WopiReadOnlyGrant grant)
    {
        ValidateDocument(doc);
        ArgumentNullException.ThrowIfNull(grant);
        if (doc.DocumentId != grant.DocumentId ||
            doc.RevisionId != grant.RevisionId || doc.UserId != grant.UserId)
            throw new UnauthorizedAccessException("WOPI GetFile: нет доступа к данной редакции.");
        return new WopiGetFilePlan(doc.DocumentId, doc.RevisionId, doc.FileSize);
    }

    public static void PlanPutFile(WopiReadOnlyGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        throw new NotSupportedException(
            "Запись через WOPI/PutFile запрещена до реализации серверных ACL, " +
            "блокировок и сохранения новой редакции.");
    }

    private static void ValidateDocument(WopiDocument? d)
    {
        ArgumentNullException.ThrowIfNull(d);
        foreach (string? value in new[] {
                     d.DocumentId, d.RevisionId, d.OwnerId, d.UserId, d.UserFriendlyName, d.FileName
                 })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
                value.Any(c => char.IsControl(c) ||
                    char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
                throw new ArgumentException("Недопустимое поле документа для WOPI.");
        }
        if (d.FileName.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("В WOPI передаётся только имя файла без пути.");
        if (Path.GetExtension(d.FileName).ToLowerInvariant() is not (".docx" or ".xlsx" or ".pptx"))
            throw new NotSupportedException("В этом WOPI-прототипе доступны DOCX/XLSX/PPTX.");
        if (d.FileSize < 0 || d.FileSize > 256L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(d.FileSize));
    }
}
