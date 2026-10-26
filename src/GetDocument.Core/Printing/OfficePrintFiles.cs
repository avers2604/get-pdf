namespace GetDocument.Core.Printing;

/// <summary>
/// Temporary PDF exports produced exclusively from an editable Office document
/// for a single GET-owned print-wizard request. The user document is never
/// handled as a disposable export.
/// </summary>
public static class OfficePrintFiles
{
    public static string ExportFolder
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                throw new IOException("Каталог текущего пользователя недоступен.");
            return Path.GetFullPath(Path.Combine(root, "GET-Document", "OfficePrintExports"));
        }
    }

    public static string NewExportPath()
    {
        string folder = ExportFolder;
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        DeleteExpiredExports(folder);
        return Path.Combine(folder, "office-" + Guid.NewGuid().ToString("N") + ".pdf");
    }

    public static void HardenExport(string path)
    {
        if (!IsOwnedExport(path) || !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Недопустимый временный PDF.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void DeleteExpiredExports(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "office-*.pdf",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!IsOwnedExport(file)) continue;
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7))
                    continue;
                info.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static bool IsOwnedExport(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), ExportFolder, comparison))
            return false;
        string filename = Path.GetFileName(full);
        if (!filename.StartsWith("office-", StringComparison.Ordinal) ||
            !filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
            filename.Length != "office-".Length + 32 + ".pdf".Length)
            return false;
        return Guid.TryParseExact(filename.Substring("office-".Length, 32), "N", out _);
    }

    /// <summary>Never delete user PDFs or files outside the dedicated export folder.</summary>
    public static void DeleteOwnedExport(string? path)
    {
        if (!IsOwnedExport(path)) return;
        try { File.Delete(path!); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
