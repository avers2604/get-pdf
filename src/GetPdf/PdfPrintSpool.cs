namespace GetPdf;

/// <summary>
/// Ephemeral user-local page images for WPF print. Each PNG is flushed before
/// its corresponding decoded NAPS2 image is disposed. Keeps only file names,
/// enforces a separate disk-space budget, and removes its private folder.
/// No file contains document text in application logs.
/// </summary>
internal sealed class PdfPrintSpool : IDisposable
{
    private const long DefaultMaxBytes = 4L * 1024 * 1024 * 1024;
    private readonly List<string> _pages = [];
    private readonly long _maxSpoolBytes;
    private long _totalBytes;
    private bool _disposed;

    private PdfPrintSpool(string directoryPath, long maxSpoolBytes)
    {
        DirectoryPath = directoryPath;
        _maxSpoolBytes = maxSpoolBytes;
    }

    public string DirectoryPath { get; }
    public IReadOnlyList<string> Pages => _pages.AsReadOnly();

    public static PdfPrintSpool Create(string sessionFolder, long maxSpoolBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionFolder);
        if (maxSpoolBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxSpoolBytes));
        var session = Path.GetFullPath(sessionFolder);
        if (!Directory.Exists(session)) throw new DirectoryNotFoundException(session);
        var folder = Path.Combine(session, "print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return new PdfPrintSpool(folder, maxSpoolBytes);
    }

    public void AddFile(string fileName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.GetFileName(fileName) != fileName ||
            !fileName.StartsWith("recovery-", StringComparison.Ordinal) ||
            !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Недопустимое имя временной страницы печати.", nameof(fileName));
        var fullPath = Path.Combine(DirectoryPath, fileName);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Страница печати не создана.", fullPath);
        long size = new FileInfo(fullPath).Length;
        if (size < 1 || size > _maxSpoolBytes - _totalBytes)
            throw new InvalidOperationException(
                "Недостаточно дискового бюджета для подготовки PDF к печати. " +
                "Уменьшите диапазон. Временные файлы будут очищены.");
        _totalBytes += size;
        _pages.Add(fullPath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pages.Clear();
        try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Scratch folder remains under a GET-PDF session; startup maintenance
            // eventually cleans abandoned folders even after a machine crash.
        }
    }
}
