using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetPdf;

/// <summary>Stores bounded page indices under a SHA-256 path identity; no PDF content is persisted.</summary>
internal static class PdfPageBookmarks
{
    private const int MaxBookmarks = 256;
    private const int MaxBytes = 4096;
    private static string StoreFile(string documentPath)
    {
        string full = Path.GetFullPath(documentPath).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GET-PDF", "bookmarks", hash + ".json");
    }

    public static IReadOnlyList<int> Read(string documentPath) => ReadFrom(StoreFile(documentPath));

    internal static IReadOnlyList<int> ReadFrom(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > MaxBytes) return [];
            var values = JsonSerializer.Deserialize<int[]>(File.ReadAllText(file));
            return (values ?? []).Where(i => i >= 0 && i < 10000).Distinct()
                .OrderBy(i => i).Take(MaxBookmarks).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException)
        { return []; }
    }

    public static bool Toggle(string documentPath, int page) =>
        ToggleIn(StoreFile(documentPath), page);

    internal static bool ToggleIn(string file, int page)
    {
        if (page is < 0 or >= 10000) throw new ArgumentOutOfRangeException(nameof(page));
        var items = ReadFrom(file).ToList();
        bool added;
        if (items.Contains(page)) { items.Remove(page); added = false; }
        else
        {
            if (items.Count >= MaxBookmarks)
                throw new InvalidOperationException("Достигнут лимит закладок.");
            items.Add(page); added = true;
        }
        items.Sort();
        string directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        Directory.CreateDirectory(directory);
        string stage = Path.Combine(directory, ".bookmark-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(stage, JsonSerializer.Serialize(items));
            File.Move(stage, file, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
        return added;
    }
}