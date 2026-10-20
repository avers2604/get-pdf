using NAPS2.Images;
using NAPS2.ImportExport;
using NAPS2.Pdf;
using NAPS2.Scan;

namespace GetPdf;

/// <summary>
/// Lazily imports individual PDF pages using the existing NAPS2 PDFium backend.
/// Only three ProcessedImage page objects remain in memory. Page count comes
/// from PdfPig and is read once without decoding page bitmaps.
/// </summary>
internal sealed class PdfLazyPageStore : IDisposable
{
    private readonly ScanningContext _context;
    private readonly string _path;
    private readonly PdfLruCache<int, ProcessedImage> _pages =
        new(3, image => image.Dispose());
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private bool _disposed;

    public int PageCount { get; }
    public int CachedPageCount => _pages.Count;

    public PdfLazyPageStore(ScanningContext context, string path)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _path = Path.GetFullPath(path);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(_path);
        PageCount = pdf.NumberOfPages;
        if (PageCount < 1) throw new InvalidDataException("PDF не содержит страниц.");
    }

    public async Task<ProcessedImage> GetPageAsync(int zeroBasedPage)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PdfLazyPageStore));
        if (zeroBasedPage < 0 || zeroBasedPage >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedPage));
        await _mutex.WaitAsync();
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PdfLazyPageStore));
            if (_pages.TryGet(zeroBasedPage, out var hit) && hit is not null)
                return hit;
            var importer = new PdfImporter(_context);
            ProcessedImage? result = null;
            try
            {
                await foreach (var image in importer.Import(_path,
                    new ImportParams { Slice = Slice.Item(zeroBasedPage) }))
                {
                    if (result is not null)
                    {
                        image.Dispose();
                        throw new InvalidDataException("Выборочный импорт PDF вернул больше одной страницы.");
                    }
                    result = image;
                }
                if (result is null)
                    throw new InvalidDataException("Не удалось загрузить выбранную страницу PDF.");
                _pages.Add(zeroBasedPage, result);
                return result;
            }
            catch
            {
                if (result is not null && !_pages.TryGet(zeroBasedPage, out _))
                    result.Dispose();
                throw;
            }
        }
        finally { _mutex.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Caller must wait for active GetPageAsync before disposing the store.
        _disposed = true;
        _pages.Dispose();
        _mutex.Dispose();
    }
}
