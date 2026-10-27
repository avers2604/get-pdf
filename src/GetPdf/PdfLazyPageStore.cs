using System.Windows.Media.Imaging;

namespace GetPdf;

/// <summary>
/// Renders individual PDF pages on demand, straight at preview or thumbnail size
/// (<see cref="PdfPageRasterizer"/>), so a large-format sheet never needs a
/// full-resolution intermediate bitmap. Page count comes from PdfPig and is read
/// once without decoding page content.
/// </summary>
internal sealed class PdfLazyPageStore : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private bool _disposed;

    public int PageCount { get; }

    public PdfLazyPageStore(string path)
    {
        _path = Path.GetFullPath(path);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(_path);
        PageCount = pdf.NumberOfPages;
        if (PageCount < 1) throw new InvalidDataException("PDF не содержит страниц.");
    }

    /// <summary>Screen preview of a page, within <see cref="PdfPreviewBounds"/>.</summary>
    public Task<BitmapSource> RenderPreviewAsync(int zeroBasedPage, CancellationToken cancellation = default) =>
        RenderAsync(zeroBasedPage, PdfPreviewBounds.Fit, cancellation);

    /// <summary>Thumbnail of a page, within <see cref="PdfThumbnailBounds"/>.</summary>
    public Task<BitmapSource> RenderThumbnailAsync(int zeroBasedPage, CancellationToken cancellation = default) =>
        RenderAsync(zeroBasedPage, PdfThumbnailBounds.Fit, cancellation);

    private async Task<BitmapSource> RenderAsync(int zeroBasedPage,
        Func<int, int, (int Width, int Height)> fit, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_disposed) throw new ObjectDisposedException(nameof(PdfLazyPageStore));
        if (zeroBasedPage < 0 || zeroBasedPage >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedPage));
        await _mutex.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(PdfLazyPageStore));
            // PDFium is not thread-safe: own it exclusively against NAPS2 work in other tabs.
            using var exclusive = await PdfRenderConcurrency.EnterExclusiveAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            return await Task.Run(() => PdfPageRasterizer.Render(_path, zeroBasedPage, fit), cancellation);
        }
        finally { _mutex.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Caller must wait for active renders before disposing the store.
        _disposed = true;
        _mutex.Dispose();
    }
}
