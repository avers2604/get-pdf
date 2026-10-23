using System.Text.Json;

namespace GetPdf;

/// <summary>One remembered opening: the absolute PDF path and when it was last opened.</summary>
internal sealed record PdfRecentEntry(string Path, DateTime? OpenedUtc);

/// <summary>
/// Bounded, explicitly user-local recent document paths. No PDF contents, credentials or
/// thumbnails are persisted. The caller only records a document after successful opening.
/// </summary>
internal static class PdfRecentDocuments
{
    private const int MaxEntries = 16;
    private static string DefaultPath => PdfPathListFile.InAppData("recent.json");

    public static IReadOnlyList<PdfRecentEntry> Read() => ReadFrom(DefaultPath);

    internal static IReadOnlyList<PdfRecentEntry> ReadFrom(string path)
    {
        string? json = PdfPathListFile.ReadText(path);
        if (json is null) return Array.Empty<PdfRecentEntry>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<PdfRecentEntry>();
            var entries = new List<PdfRecentEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                // 0.1.1 and earlier stored bare paths; keep reading them after an upgrade.
                if (item.ValueKind == JsonValueKind.String)
                    entries.Add(new PdfRecentEntry(item.GetString()!, null));
                else if (item.ValueKind == JsonValueKind.Object &&
                         item.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    DateTime? opened = item.TryGetProperty("OpenedUtc", out var o) &&
                        o.ValueKind == JsonValueKind.String && o.TryGetDateTime(out var at)
                        ? DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc) : null;
                    entries.Add(new PdfRecentEntry(p.GetString()!, opened));
                }
            }
            return entries.Where(e => PdfPathListFile.IsValid(e.Path))
                .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries).ToArray();
        }
        catch (JsonException) { return Array.Empty<PdfRecentEntry>(); }
    }

    public static void Remember(string path) => RememberIn(DefaultPath, path, DateTime.UtcNow);

    internal static void RememberIn(string destination, string path, DateTime? openedUtc = null)
    {
        path = PdfPathListFile.Normalize(path);
        var entries = new[] { new PdfRecentEntry(path, openedUtc ?? DateTime.UtcNow) }
            .Concat(ReadFrom(destination))
            .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(MaxEntries).ToArray();
        PdfPathListFile.WriteAtomically(destination, JsonSerializer.Serialize(entries));
    }

    public static void Forget(string path) => ForgetIn(DefaultPath, path);

    internal static void ForgetIn(string destination, string path)
    {
        var entries = ReadFrom(destination)
            .Where(e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        PdfPathListFile.WriteAtomically(destination, JsonSerializer.Serialize(entries));
    }

    public static void Clear() => ClearFrom(DefaultPath);
    internal static void ClearFrom(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// Documents the operator pinned with the star. Same privacy boundary as the history:
/// absolute PDF paths only, user-local, bounded; the PDFs themselves are never copied.
/// </summary>
internal static class PdfFavoriteDocuments
{
    internal const int MaxEntries = 50;
    private static string DefaultPath => PdfPathListFile.InAppData("favorites.json");

    public static IReadOnlyList<string> Read() => ReadFrom(DefaultPath);

    internal static IReadOnlyList<string> ReadFrom(string path)
    {
        string? json = PdfPathListFile.ReadText(path);
        if (json is null) return Array.Empty<string>();
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Where(PdfPathListFile.IsValid).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries).ToArray();
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    public static bool Contains(string path) => ContainsIn(DefaultPath, path);
    internal static bool ContainsIn(string store, string path) =>
        ReadFrom(store).Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds or removes <paramref name="path"/>; returns true when it is now a favourite.</summary>
    public static bool Toggle(string path) => ToggleIn(DefaultPath, path);

    internal static bool ToggleIn(string store, string path)
    {
        path = PdfPathListFile.Normalize(path);
        var current = ReadFrom(store);
        bool remove = current.Contains(path, StringComparer.OrdinalIgnoreCase);
        if (!remove && current.Count >= MaxEntries)
            throw new InvalidOperationException(
                $"В избранном уже {MaxEntries} документов. Уберите ненужные, чтобы добавить новый.");
        var next = remove
            ? current.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray()
            : new[] { path }.Concat(current).ToArray();
        PdfPathListFile.WriteAtomically(store, JsonSerializer.Serialize(next));
        return !remove;
    }

    public static void Clear() => PdfRecentDocuments.ClearFrom(DefaultPath);
}

/// <summary>Shared bounded read / atomic write for the small per-user path lists.</summary>
internal static class PdfPathListFile
{
    private const long MaxFileBytes = 64 * 1024;

    internal static string InAppData(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GET-PDF", name);

    internal static bool IsValid(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 8192 &&
        Path.IsPathFullyQualified(path) &&
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        if (!IsValid(path)) throw new ArgumentException("Только абсолютные PDF-пути.", nameof(path));
        return path;
    }

    internal static string? ReadText(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException)
        { return null; }
    }

    internal static void WriteAtomically(string destination, string json)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        string stage = Path.Combine(folder, ".list-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(stage, json);
            File.Move(stage, destination, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
