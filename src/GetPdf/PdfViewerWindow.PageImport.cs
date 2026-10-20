using System.Windows;
using System.Windows.Media.Imaging;
using NAPS2.Images;

namespace GetPdf;

public partial class PdfViewerWindow
{
    /// <summary>
    /// Imports an approved source page into the destination's private,
    /// append-only source PDF. The originals remain immutable. Insertion is
    /// registered with PdfEditHistory so Ctrl+Z/Redo work without re-importing.
    /// </summary>
    internal async Task InsertExternalPageInDraftAsync(
        PdfCrossTabSnapshot source, Guid pageId,
        PdfCrossTabSnapshot destination, CancellationToken token)
    {
        if (_plan is null || _history is null || _context is null ||
            _sessionFolder is null || _sourcePath is null ||
            !string.Equals(_sourcePath, destination.WorkingPath,
                StringComparison.OrdinalIgnoreCase) ||
            _plan.SourcePageCount != destination.SourcePageCount ||
            _plan.Pages.Count != destination.Pages.Length)
            throw new InvalidOperationException("Документ назначения изменился, повторите перенос.");

        int sourceIndex = PdfCrossTabPageTransfer.FindPageById(source.Pages, pageId);
        if (sourceIndex < 0)
            throw new InvalidOperationException("Исходная страница больше недоступна.");
        var reference = source.Pages[sourceIndex];
        if (reference.Rotation != 0 || reference.Stamp.Length != 0 ||
            reference.Rectangles is { Count: > 0 } ||
            reference.Notes is { Count: > 0 })
            throw new InvalidOperationException(
                "Перед переносом сохраните исходный PDF с поворотами или заметками.");
        if (_plan.SourcePageCount >= 10000 ||
            _plan.Pages.Count >= WorkstationPolicy.Current.MaxDocumentPages)
            throw new InvalidOperationException("Превышен лимит исходных или видимых страниц.");

        string one = Path.Combine(_sessionFolder,
            "cross-page-" + Guid.NewGuid().ToString("N") + ".pdf");
        string master = Path.Combine(_sessionFolder,
            "text-master-" + Guid.NewGuid().ToString("N") + ".pdf");

        PdfLazyPageStore? replacement = null;
        bool adopted = false;
        try
        {
            // All expensive parsing and writer work happens off the UI thread.
            // Writer rejects signed/interactively unsafe source structures.
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                PdfDocumentWriter.SaveAs(source.WorkingPath, one, [reference], token);
                token.ThrowIfCancellationRequested();
                PdfDocumentWriter.AppendSourceRevision(destination.WorkingPath, one, master);
                token.ThrowIfCancellationRequested();
                PdfOutputVerifier.Verify(master, destination.SourcePageCount + 1);
            }, token);

            // Preflight both the PDF structure and layout before modifying
            // existing per-document caches and source references.
            bool[] markup = await Task.Run(() =>
                PdfMarkupPolicy.ReadEligibleSourcePages(master), token);
            System.Windows.Size[] sizes = await Task.Run(() =>
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(master);
                return pdf.GetPages().Select(page => new System.Windows.Size(
                    page.Width * 96 / 72, page.Height * 96 / 72)).ToArray();
            }, token);
            if (sizes.Length != destination.SourcePageCount + 1 ||
                markup.Length != sizes.Length)
                throw new InvalidDataException("Несогласованная таблица добавленных PDF-страниц.");

            token.ThrowIfCancellationRequested();
            if (!string.Equals(_sourcePath, destination.WorkingPath,
                StringComparison.OrdinalIgnoreCase) ||
                _plan.SourcePageCount != destination.SourcePageCount ||
                _plan.Pages.Count != destination.Pages.Length)
                throw new InvalidOperationException("PDF изменился во время переноса.");

            await StopThumbnailWorkerAsync();
            await DocumentView.StopAsync();
            replacement = new PdfLazyPageStore(_context, master);
            if (replacement.PageCount != sizes.Length)
                throw new InvalidDataException("Количество импортированных страниц изменилось.");

            // From this point the new private source is durable and
            // validated; undo only adjusts the visible page references.
            var oldStore = _pageStore;
            _pageStore = replacement;
            _sourcePath = master;
            _sourcePageSizes = sizes;
            _markupAllowed = markup;
            adopted = true;
            replacement = null;
            oldStore?.Dispose();

            int appended = _plan.RegisterSourcePage();
            _history.Execute(p => p.InsertSourcePageAfter(
                destination.SelectedPageIndex, appended));
            _pageIndex = destination.SelectedPageIndex + 1;
            _textIndex = null;
            _textReadKey = null;
            _searchGeometryCache.Clear();
            _continuousFingerprint = null;
            _thumbnailCache?.Dispose();
            _thumbnailCache = new PdfLruCache<(int, int), BitmapSource>(12, _ => { },
                48L * 1024 * 1024, EstimateBitmapBytes);
            _thumbnailItems = [];
            RefreshList();
            if (!PersistPdfDraft())
                StatusText.Text = "Страница вставлена, но аварийная копия черновика не сохранена.";
            else
                StatusText.Text = "Страница вставлена в черновик PDF. Ctrl+Z отменяет вставку.";
        }
        finally
        {
            replacement?.Dispose();
            try { if (File.Exists(one)) File.Delete(one); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { DiagnosticLog.Failure("pdf-cross-page-cleanup", ex); }
            // Keep master if adopted; Undo/Redo and draft recovery still
            // depend on its append-only source references.
            if (!adopted)
            {
                try { if (File.Exists(master)) File.Delete(master); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { DiagnosticLog.Failure("pdf-cross-master-cleanup", ex); }
            }
            EnsureThumbnailWorker();
        }
    }
}
