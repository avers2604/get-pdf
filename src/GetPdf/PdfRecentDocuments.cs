using System.Text.Json;

namespace GetPdf;

/// <summary>
/// Bounded, explicitly user-local recent document paths. No PDF contents, credentials or
/// thumbnails are persisted. The caller only records a document after successful opening.
/// </summary>
internal static class PdfRecentDocuments
{
    private const int MaxEntries = 16;
    private const long MaxFileBytes = 64 * 1024;
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GET-PDF", "recent.json");

    public static IReadOnlyList<string> Read() => ReadFrom(DefaultPath);

    internal static IReadOnlyList<string> ReadFrom(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes)
                return Array.Empty<string>();
            var entries = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
            return (entries ?? []).Where(IsValid).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   JsonException or ArgumentException or NotSupportedException)
        { return Array.Empty<string>(); }
    }

    private static bool IsValid(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 8192 &&
        Path.IsPathFullyQualified(path) &&
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static void Remember(string path) => RememberIn(DefaultPath, path);

    internal static void RememberIn(string destination, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        if (!IsValid(path)) throw new ArgumentException("Только абсолютные PDF-пути.", nameof(path));
        var entries = new[] { path }.Concat(ReadFrom(destination))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxEntries).ToArray();
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        string stage = Path.Combine(folder, ".recent-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(stage, JsonSerializer.Serialize(entries));
            File.Move(stage, destination, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    public static void Clear() => ClearFrom(DefaultPath);
    internal static void ClearFrom(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}