namespace GetPdf;

/// <summary>
/// User-local, disposable storage for short OCR PDFs produced by NAPS2.
/// The total allowed temporary storage is bounded; only sequential filenames
/// managed by this component can be registered. No document names logged.
/// </summary>
internal sealed class PdfOcrScratch : IDisposable
{
    private const long DefaultMaxBytes = 4L * 1024 * 1024 * 1024;
    private readonly List<string> _parts = [];
    private readonly long _limit;
    private long _bytes;
    private bool _disposed;

    private PdfOcrScratch(string directory, long limit)
    {
        DirectoryPath = directory;
        _limit = limit;
    }

    public string DirectoryPath { get; }
    public IReadOnlyList<string> ChunkFiles => _parts.AsReadOnly();

    public static PdfOcrScratch Create(string sessionFolder, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionFolder);
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var session = Path.GetFullPath(sessionFolder);
        if (!Directory.Exists(session)) throw new DirectoryNotFoundException(session);
        var directory = Path.Combine(session, "ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new PdfOcrScratch(directory, maxBytes);
    }

    public string GetChunkPath(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        return Path.Combine(DirectoryPath, $"chunk-{index:D5}.pdf");
    }

    public void RegisterChunk(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index != _parts.Count)
            throw new InvalidOperationException("OCR-фрагменты должны создаваться строго по порядку.");
        var path = GetChunkPath(index);
        if (!File.Exists(path))
            throw new FileNotFoundException("PDF-фрагмент отсутствует.", path);
        long length = new FileInfo(path).Length;
        if (length <= 0 || length > _limit - _bytes)
            throw new InvalidOperationException("Превышено ограничение временного дискового пространства OCR.");
        _bytes += length;
        _parts.Add(path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parts.Clear();
        try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Abandoned data remains under GET-PDF session cleanup rules.
        }
    }
}
