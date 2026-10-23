using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Collaboration;

/// <summary>
/// Server-side parameters supplied only after the internal gateway authenticates
/// the user, checks ACL and issues short-lived authorized content/callback URLs.
/// </summary>
public sealed record OnlyOfficeSessionOptions(
    string DocumentId,
    string RevisionId,
    string DisplayFileName,
    string UserId,
    string UserDisplayName,
    Uri StorageOrigin,
    Uri DocumentUrl,
    Uri CallbackUrl,
    Uri DocumentServerOrigin,
    bool CanEdit,
    bool IsWormOriginal);

public sealed record OnlyOfficeEditorBootstrap(string DocumentKey, string ConfigurationJson);

public static class OnlyOfficeSessionFactory
{
    public static OnlyOfficeEditorBootstrap Create(OnlyOfficeSessionOptions options, byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (signingKey is null || signingKey.Length < 32)
            throw new ArgumentException("Требуется секрет сервера не менее 32 байт.", nameof(signingKey));
        string id = Required(options.DocumentId, 128);
        string revision = Required(options.RevisionId, 128);
        string title = Required(options.DisplayFileName, 200);
        if (title.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("Название документа не должно содержать путь.");
        string userId = Required(options.UserId, 128);
        string userName = Required(options.UserDisplayName, 200);

        string ext = Path.GetExtension(title).ToLowerInvariant();
        string type = ext switch
        {
            ".docx" => "word",
            ".xlsx" => "cell",
            ".pptx" => "slide",
            _ => throw new NotSupportedException("Онлайн-движок пока разрешён только для DOCX, XLSX и PPTX.")
        };
        OnlyOfficeUriPolicy.RequireHttpsOrigin(options.StorageOrigin);
        OnlyOfficeUriPolicy.RequireHttpsOrigin(options.DocumentServerOrigin);
        OnlyOfficeUriPolicy.RequireTrustedUrl(options.DocumentUrl, options.StorageOrigin);
        OnlyOfficeUriPolicy.RequireTrustedUrl(options.CallbackUrl, options.StorageOrigin);

        bool edit = options.CanEdit && !options.IsWormOriginal;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            id + "\n" + revision))).ToLowerInvariant();
        // document.key is revision-specific to prevent the editor reusing stale data.
        // No SMB/UNC path or signing secret enters this configuration.
        var payload = new Dictionary<string, object>
        {
            ["documentType"] = type,
            ["type"] = "desktop",
            ["document"] = new
            {
                fileType = ext[1..],
                key,
                title,
                url = options.DocumentUrl.AbsoluteUri,
                permissions = new { edit, download = false, print = false }
            },
            ["editorConfig"] = new
            {
                callbackUrl = options.CallbackUrl.AbsoluteUri,
                mode = edit ? "edit" : "view",
                lang = "ru",
                user = new { id = userId, name = userName }
            }
        };
        string token = Hs256Jwt.Sign(payload, signingKey);
        payload["token"] = token;
        return new OnlyOfficeEditorBootstrap(key, JsonSerializer.Serialize(payload));
    }

    private static string Required(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max ||
            value.Any(ch => char.IsControl(ch) || char.IsSurrogate(ch) ||
                char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format))
            throw new ArgumentException("Некорректное или слишком длинное поле сеанса.");
        return value;
    }
}

/// <summary>
/// This check is not SSRF prevention by itself: the internal download gateway
/// must pin DNS/addresses, disable redirects, authenticate and enforce byte limits.
/// </summary>
public static class OnlyOfficeUriPolicy
{
    public static void RequireHttpsOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
            origin.AbsolutePath != "/" || origin.Query.Length > 0 ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.Fragment.Length > 0)
            throw new ArgumentException("Ожидается явно разрешённый HTTPS origin.", nameof(origin));
    }

    public static void RequireTrustedUrl(Uri url, Uri expectedOrigin)
    {
        ArgumentNullException.ThrowIfNull(url);
        RequireHttpsOrigin(expectedOrigin);
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment) ||
            !string.Equals(url.GetLeftPart(UriPartial.Authority),
                expectedOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("URL не принадлежит утверждённому HTTPS хосту.", nameof(url));
    }
}
