using System.Text.Json;

namespace GetPdf;

/// <summary>Only UI layout is persisted. Document paths, content, user names and scanner settings are excluded.</summary>
public sealed record PdfWorkspacePreferences(int Version = 1, string ActiveTool = "search");

public static class PdfWorkspaceToolGroups
{
    private static readonly string[] Ids = ["search", "markup", "pages", "document"];

    public static string FromIndex(int index) =>
        index >= 0 && index < Ids.Length ? Ids[index] : Ids[0];

    public static int ToIndex(string? id)
    {
        for (int i = 0; i < Ids.Length; i++)
            if (string.Equals(Ids[i], id, StringComparison.Ordinal)) return i;
        return 0;
    }

    public static bool IsKnown(string? id) =>
        id is not null && Ids.Contains(id, StringComparer.Ordinal);
}

public static class PdfWorkspacePreferencesStore
{
    private const int MaxFileBytes = 4096;
    private static string PreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GET-PDF", "pdf-workspace.json");

    public static PdfWorkspacePreferences Load() => LoadFrom(PreferencesPath);

    public static PdfWorkspacePreferences LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes)
                return new PdfWorkspacePreferences();
            var result = JsonSerializer.Deserialize<PdfWorkspacePreferences>(File.ReadAllText(path));
            return result is { Version: 1 } && PdfWorkspaceToolGroups.IsKnown(result.ActiveTool)
                ? result : new PdfWorkspacePreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        {
            // Preferences failure must not change scanner selection or prevent document viewing.
            return new PdfWorkspacePreferences();
        }
    }

    public static void Save(PdfWorkspacePreferences preferences) =>
        SaveTo(PreferencesPath, preferences);

    public static void SaveTo(string path, PdfWorkspacePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var safe = new PdfWorkspacePreferences(1,
            PdfWorkspaceToolGroups.IsKnown(preferences.ActiveTool)
                ? preferences.ActiveTool : "search");
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string staging = Path.Combine(directory, ".pdf-workspace-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(safe));
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
