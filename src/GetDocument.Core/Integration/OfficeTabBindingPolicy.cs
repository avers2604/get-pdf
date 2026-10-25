using System.Text.Json;

namespace GetDocument.Core.Integration;

/// <summary>
/// Independent, read-only tab association policy. Never inherits the
/// upload capability: IT may enable tab binding while uploads remain off.
/// </summary>
public sealed record OfficeTabBindingPolicy(bool Enabled, Uri? DocumentsUri)
{
    public static OfficeTabBindingPolicy Disabled { get; } = new(false, null);

    public Uri Destination()
    {
        if (!Enabled || DocumentsUri is null || !AllowedUrl(DocumentsUri))
            throw new InvalidOperationException("Привязка вкладки к GUP-2 отключена.");
        return new Uri(DocumentsUri, "getdocument/tab-binding/redeem/");
    }

    public static OfficeTabBindingPolicy Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int v) || v != 1)
            throw new InvalidDataException("Неподдерживаемая политика ГЭТ Документ.");

        bool enabled = root.TryGetProperty("AllowGup2TabBinding", out var flag) &&
                       flag.ValueKind == JsonValueKind.True;
        if (!enabled) return Disabled;

        if (!root.TryGetProperty("Gup2DocumentsUrl", out var target) ||
            target.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(target.GetString(), UriKind.Absolute, out var uri) ||
            !AllowedUrl(uri))
            throw new InvalidDataException("Нужен утверждённый HTTPS-адрес документов GUP-2.");
        return new(true, uri);
    }

    private static bool AllowedUrl(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(uri.Host) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.EndsWith("/documents/", StringComparison.Ordinal) &&
        !uri.AbsolutePath.Contains('%') && !uri.AbsolutePath.Contains("//", StringComparison.Ordinal);

    public static OfficeTabBindingPolicy LoadLocal()
    {
        string path;
        if (OperatingSystem.IsWindows())
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(root)) return Disabled;
            path = Path.Combine(root, "GET-Document", "policy.json");
        }
        else if (OperatingSystem.IsLinux())
            path = "/etc/get-document/policy.json";
        else
            return Disabled;

        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : Disabled; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                    ArgumentException or JsonException or InvalidDataException)
        { return Disabled; }
    }
}
