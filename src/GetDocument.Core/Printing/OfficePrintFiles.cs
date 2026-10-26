namespace GetDocument.Core.Printing;

/// <summary>
/// Temporary PDF exports produced exclusively from an editable Office document
/// for a single GET-owned print-wizard request. The user document is never
/// handled as a disposable export.
/// </summary>
public static class OfficePrintFiles
{
    public static string ExportFolder =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GET-Document-print"));

    public static string NewExportPath()
    {
        Directory.CreateDirectory(ExportFolder);
        return Path.Combine(ExportFolder, "office-" + Guid.NewGuid().ToString("N") + ".pdf");
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
