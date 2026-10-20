using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GetPdf;

/// <summary>
/// Prints one PDF page at a time. Raster printing is intentional and clearly documented;
/// source page objects are not modified, but vector text is not sent to the printer.
/// </summary>
internal sealed class PdfPagePrintPaginator : DocumentPaginator
{
    private readonly IReadOnlyList<string> _rasterFiles;
    private readonly IReadOnlyList<PdfPageReference> _plannedPages;

    public PdfPagePrintPaginator(IReadOnlyList<string> rasterFiles,
        IReadOnlyList<PdfPageReference> plannedPages, Size printableSize)
    {
        ArgumentNullException.ThrowIfNull(rasterFiles);
        ArgumentNullException.ThrowIfNull(plannedPages);
        if (!double.IsFinite(printableSize.Width) || !double.IsFinite(printableSize.Height) ||
            printableSize.Width <= 0 || printableSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(printableSize));
        if (rasterFiles.Count < 1 || plannedPages.Count < 1)
            throw new ArgumentException("Нет страниц для печати.");
        foreach (var page in plannedPages)
        {
            if (page is null || page.SourcePage < 0 || page.SourcePage >= rasterFiles.Count ||
                string.IsNullOrWhiteSpace(rasterFiles[page.SourcePage]))
                throw new ArgumentException("Неверная таблица растровых страниц печати.");
        }
        _rasterFiles = rasterFiles.ToArray();
        _plannedPages = plannedPages.ToArray();
        PageSize = printableSize;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _plannedPages.Count;
    public override Size PageSize { get; set; }
    public override IDocumentPaginatorSource Source => null!;
    public override void ComputePageCount() { }

    public override DocumentPage GetPage(int pageNumber)
    {
        if (pageNumber < 0 || pageNumber >= _plannedPages.Count)
            return DocumentPage.Missing;
        var planned = _plannedPages[pageNumber];
        // Decode one staged page only when WPF asks the paginator for it.
        // The PNG stream is closed immediately (OnLoad), so no source files
        // need to remain open beyond this GetPage invocation.
        using var stream = File.OpenRead(_rasterFiles[planned.SourcePage]);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource image = decoder.Frames[0];
        if (image.CanFreeze) image.Freeze();
        if (planned.Rotation != 0)
            image = new TransformedBitmap(image, new RotateTransform(planned.Rotation));

        // PrintableArea excludes unprintable printer margins; preserve aspect ratio.
        double margin = 12;
        double availableWidth = Math.Max(1, PageSize.Width - margin * 2);
        double availableHeight = Math.Max(1, PageSize.Height - margin * 2);
        double scale = Math.Min(availableWidth / image.PixelWidth, availableHeight / image.PixelHeight);
        double width = image.PixelWidth * scale;
        double height = image.PixelHeight * scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect((PageSize.Width - width) / 2,
                (PageSize.Height - height) / 2, width, height));
            // Printed vector outlines must match the coordinates shown by the PDF viewer.
            if (planned.Rectangles is { Count: > 0 })
            {
                double originX = (PageSize.Width - width) / 2;
                double originY = (PageSize.Height - height) / 2;
                var pen = new Pen(Brushes.Red, 2);
                foreach (var rectangle in planned.Rectangles)
                    dc.DrawRectangle(null, pen, new Rect(
                        originX + rectangle.X * width,
                        originY + rectangle.Y * height,
                        rectangle.Width * width,
                        rectangle.Height * height));
            }
            if (planned.Notes is { Count: > 0 })
            {
                double originX = (PageSize.Width - width) / 2;
                double originY = (PageSize.Height - height) / 2;
                foreach (var note in planned.Notes)
                {
                    var noteRect = new Rect(originX + note.X * width, originY + note.Y * height,
                        .4 * width, .12 * height);
                    dc.DrawRectangle(Brushes.LightYellow, new Pen(Brushes.DarkRed, 1), noteRect);
                    var text = new FormattedText(note.Text, CultureInfo.GetCultureInfo("ru-RU"),
                        FlowDirection.LeftToRight, new Typeface("Arial"), 10, Brushes.DarkRed, 1.0)
                    {
                        MaxTextWidth = Math.Max(1, noteRect.Width - 8),
                        MaxTextHeight = Math.Max(1, noteRect.Height - 6),
                        Trimming = TextTrimming.CharacterEllipsis
                    };
                    dc.DrawText(text, new Point(noteRect.X + 4, noteRect.Y + 3));
                }
            }
            if (!string.IsNullOrWhiteSpace(planned.Stamp))
            {
                // Print-preview overlay; this is not a cryptographic stamp or PDF annotation.
                var label = new FormattedText(planned.Stamp,
                    CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                    new Typeface("Arial"), 16, Brushes.DarkRed, 1.0);
                dc.DrawText(label, new Point(Math.Max(0, (PageSize.Width - width) / 2) + 16,
                    Math.Max(0, (PageSize.Height - height) / 2) + 16));
            }
        }
        return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
    }
}
