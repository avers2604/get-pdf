using System.Text;
using System.Text.Json;

namespace GetPdf;

/// <summary>
/// Local GET-system integration handoff, schema v1. No network calls,
/// authentication tokens, personal information, file paths or PDF text.
/// Writing a status does not imply any GET-system server is connected.
/// </summary>
internal sealed record PdfGetSystemStatusSnapshot(
    string Schema,
    string Product,
    string ApplicationVersion,
    DateTimeOffset GeneratedUtc,
    int OpenWindows,
    int OpenDocuments,
    bool PrintingAllowed,
    bool OfflineOnly);

internal static class PdfGetSystemStatusWriter
{
    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GET-PDF", "integration", "getpdf-status.v1.json");

    internal static PdfGetSystemStatusSnapshot Create(
        DateTimeOffset now, int openWindows, int openDocuments, bool printingAllowed)
    {
        if (openWindows is < 0 or > 256 || openDocuments is < 0 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(openDocuments));
        if (openWindows == 0 && openDocuments > 0)
            throw new ArgumentException("Нельзя сообщать открытые PDF без окна приложения.");
        string version = typeof(PdfGetSystemStatusWriter).Assembly
            .GetName().Version?.ToString(3) ?? "0.0.0";
        return new PdfGetSystemStatusSnapshot(
            "get-system.pdf.status.v1", "GET-PDF", version, now.ToUniversalTime(),
            openWindows, openDocuments, printingAllowed, true);
    }

    internal static void Write(PdfGetSystemStatusSnapshot status) =>
        WriteTo(DefaultPath, status);

    internal static void Clear() => ClearFrom(DefaultPath);

    internal static void ClearFrom(string path)
    {
        string target = Path.GetFullPath(path);
        if (File.Exists(target)) File.Delete(target);
    }

    internal static void WriteTo(string destination, PdfGetSystemStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Schema != "get-system.pdf.status.v1" ||
            status.Product != "GET-PDF" || status.OpenWindows is < 0 or > 256 ||
            status.OpenDocuments is < 0 or > 4096 ||
            status.OpenWindows == 0 && status.OpenDocuments != 0 ||
            !status.OfflineOnly)
            throw new ArgumentException("Некорректный контракт GET-system.", nameof(status));

        string path = Path.GetFullPath(destination);
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(status);
        if (json.Length > 2048)
            throw new ArgumentException("Превышен лимит статуса.", nameof(status));
        string temporary = Path.Combine(folder,
            ".getpdf-status-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
