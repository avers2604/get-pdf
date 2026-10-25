using System.Text.Json;

namespace GetDocument.Core.Integration;

/// <summary>
/// Admin-controlled, disabled-by-default native Office return policy.
/// No user-entered URL or credentials are accepted by the upload UI.
/// </summary>
public sealed record OfficeReturnPolicy(bool Enabled, Uri? DocumentsUri, long MaxUploadBytes)
{
    public const long DefaultMaxUploadBytes = 150L * 1024 * 1024;
    public const long AbsoluteMaxUploadBytes = 256L * 1024 * 1024;
    public static OfficeReturnPolicy Disabled { get; } =
        new(false, null, DefaultMaxUploadBytes);

    public Uri Destination()
    {
        if (!Enabled || DocumentsUri is null)
            throw new InvalidOperationException("Возврат в GUP-2 запрещён политикой ИТ.");
        return new Uri(DocumentsUri, "getdocument/office-return/");
    }

    public static OfficeReturnPolicy Parse(string json)
    {
        using var source = JsonDocument.Parse(json);
        var root = source.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            version.GetInt32() != 1)
            throw new InvalidDataException("Неподдерживаемая корпоративная политика.");

        bool enabled = root.TryGetProperty("AllowGup2OfficeReturn", out var flag) &&
                       flag.ValueKind == JsonValueKind.True;
        if (!enabled) return Disabled;

        if (!root.TryGetProperty("Gup2DocumentsUrl", out var destination) ||
            destination.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Отсутствует проверенный адрес GUP-2.");

        string address = destination.GetString() ?? "";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.EndsWith("/documents/", StringComparison.Ordinal) ||
            uri.AbsolutePath.Contains('%') || uri.AbsolutePath.Contains("//", StringComparison.Ordinal))
            throw new InvalidDataException("Для интеграции требуется разрешённый HTTPS-адрес раздела документов.");

        long limit = DefaultMaxUploadBytes;
        if (root.TryGetProperty("MaxOfficeUploadBytes", out var max))
        {
            if (max.ValueKind != JsonValueKind.Number || !max.TryGetInt64(out limit) ||
                limit <= 0 || limit > AbsoluteMaxUploadBytes)
                throw new InvalidDataException("Недопустимый предел размера рабочего документа.");
        }
        return new OfficeReturnPolicy(true, uri, limit);
    }

    /// <summary>
    /// On Windows the installer must ACL ProgramData/GET-Document to admins;
    /// on Linux /etc/get-document/policy.json must be root-owned.
    /// Invalid/missing/unreadable config fails closed.
    /// </summary>
    public static OfficeReturnPolicy LoadLocal()
    {
        string path;
        if (OperatingSystem.IsWindows())
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData)) return Disabled;
            path = Path.Combine(programData, "GET-Document", "policy.json");
        }
        else if (OperatingSystem.IsLinux())
            path = "/etc/get-document/policy.json";
        else
            return Disabled;

        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : Disabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException)
        {
            return Disabled;
        }
    }
}
