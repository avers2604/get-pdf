using System.Text.Json;
using GetDocument.Core.DocumentFiles;

namespace GetDocument.Core.Workspace;

/// <summary>One remembered document: absolute path and when it was last opened.</summary>
public sealed record RecentDocument(string Path, DateTime? OpenedUtc);

/// <summary>
/// Opening history and favourites for every supported format, user-local
/// (%LOCALAPPDATA%\GET-Document). Paths only — never content, credentials or previews.
/// Bounded files written atomically; a damaged file reads as an empty list.
/// On first use the GET PDF lists (%LOCALAPPDATA%\GET-PDF) are imported once.
/// </summary>
public sealed class DocumentHistory
{
    public const int MaxRecent = 20;
    public const int MaxFavorites = 50;
    private const long MaxFileBytes = 128 * 1024;
    private readonly string _folder;
    private readonly StringComparer _paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public DocumentHistory(string? folder = null)
    {
        _folder = folder ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GET-Document");
    }

    private string RecentFile => System.IO.Path.Combine(_folder, "recent.json");
    private string FavoritesFile => System.IO.Path.Combine(_folder, "favorites.json");

    public static bool IsValid(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 8192 && System.IO.Path.IsPathFullyQualified(path) &&
        DocumentFileClassifier.Classify(path).Kind != DocumentFileKind.Unsupported;

    public IReadOnlyList<RecentDocument> Recent()
    {
        var items = Read<RecentDocument[]>(RecentFile) ?? [];
        return items.Where(i => i is not null && IsValid(i.Path)).DistinctBy(i => i.Path, _paths).Take(MaxRecent).ToArray();
    }

    public IReadOnlyList<string> Favorites()
    {
        var items = Read<string[]>(FavoritesFile) ?? [];
        return items.Where(IsValid).Distinct(_paths).Take(MaxFavorites).ToArray();
    }

    public void Remember(string path, DateTime? openedUtc = null)
    {
        path = Normalize(path);
        var list = new[] { new RecentDocument(path, openedUtc ?? DateTime.UtcNow) }.Concat(Recent())
            .DistinctBy(i => i.Path, _paths).Take(MaxRecent).ToArray();
        Write(RecentFile, list);
    }

    public void Forget(string path) =>
        Write(RecentFile, Recent().Where(i => !_paths.Equals(i.Path, path)).ToArray());

    public void ClearRecent()
    {
        if (File.Exists(RecentFile)) File.Delete(RecentFile);
    }

    public bool IsFavorite(string path) => Favorites().Contains(path, _paths);

    /// <returns>True when the document is a favourite after the call.</returns>
    public bool ToggleFavorite(string path)
    {
        path = Normalize(path);
        var current = Favorites();
        bool remove = current.Contains(path, _paths);
        if (!remove && current.Count >= MaxFavorites)
            throw new InvalidOperationException($"В избранном уже {MaxFavorites} документов. Уберите ненужные.");
        Write(FavoritesFile, remove ? current.Where(p => !_paths.Equals(p, path)).ToArray() : new[] { path }.Concat(current).ToArray());
        return !remove;
    }

    /// <summary>Imports GET PDF's history/favourites once (both formats it used).</summary>
    public void ImportFromGetPdfOnce(string? getPdfFolder = null)
    {
        string marker = System.IO.Path.Combine(_folder, ".imported-getpdf");
        if (File.Exists(marker)) return;
        getPdfFolder ??= System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GET-PDF");
        try
        {
            var favorites = Read<string[]>(System.IO.Path.Combine(getPdfFolder, "favorites.json")) ?? [];
            if (favorites.Length > 0)
                Write(FavoritesFile, Favorites().Concat(favorites.Where(IsValid)).Distinct(_paths).Take(MaxFavorites).ToArray());
            string recentFile = System.IO.Path.Combine(getPdfFolder, "recent.json");
            var recent = new List<RecentDocument>();
            if (ReadText(recentFile) is { } json)
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String) recent.Add(new RecentDocument(item.GetString()!, null));
                        else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Path", out var p) &&
                                 p.ValueKind == JsonValueKind.String)
                            recent.Add(new RecentDocument(p.GetString()!,
                                item.TryGetProperty("OpenedUtc", out var o) && o.ValueKind == JsonValueKind.String &&
                                o.TryGetDateTime(out var at) ? at.ToUniversalTime() : null));
                    }
            }
            if (recent.Count > 0)
                Write(RecentFile, Recent().Concat(recent.Where(r => IsValid(r.Path)))
                    .DistinctBy(r => r.Path, _paths).Take(MaxRecent).ToArray());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        Directory.CreateDirectory(_folder);
        File.WriteAllText(marker, "");
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = System.IO.Path.GetFullPath(path);
        if (!IsValid(path)) throw new ArgumentException("Неподдерживаемый документ.", nameof(path));
        return path;
    }

    private static string? ReadText(string file)
    {
        try { return File.Exists(file) && new FileInfo(file).Length <= MaxFileBytes ? File.ReadAllText(file) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static T? Read<T>(string file) where T : class
    {
        try { return ReadText(file) is { } json ? JsonSerializer.Deserialize<T>(json) : null; }
        catch (JsonException) { return null; }
    }

    private void Write<T>(string file, T value)
    {
        Directory.CreateDirectory(_folder);
        string stage = System.IO.Path.Combine(_folder, ".list-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(stage, JsonSerializer.Serialize(value));
            File.Move(stage, file, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
