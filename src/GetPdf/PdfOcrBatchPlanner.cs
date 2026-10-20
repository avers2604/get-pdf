namespace GetPdf;

/// <summary>
/// A contiguous slice of zero-based source PDF page indices to process together.
/// All raster estimates are advisory; native PDFium and Tesseract allocations
/// require separate monitoring on a Windows acceptance machine.
/// </summary>
internal readonly record struct PdfOcrBatch(int FirstSourcePage, int PageCount);

internal static class PdfOcrBatchPlanner
{
    internal const int DefaultMaxPages = 4;
    internal const long DefaultMaxRasterBytes = 192L * 1024 * 1024;

    public static IReadOnlyList<PdfOcrBatch> Plan(
        IReadOnlyList<PdfPageDimensions> pages,
        int maxPagesPerBatch = DefaultMaxPages,
        long maxRasterBytesPerBatch = DefaultMaxRasterBytes)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException("PDF не содержит страниц.", nameof(pages));
        if (maxPagesPerBatch < 1) throw new ArgumentOutOfRangeException(nameof(maxPagesPerBatch));
        if (maxRasterBytesPerBatch < 1) throw new ArgumentOutOfRangeException(nameof(maxRasterBytesPerBatch));

        var batches = new List<PdfOcrBatch>();
        int first = 0;
        int count = 0;
        long total = 0;

        for (int i = 0; i < pages.Count; i++)
        {
            long estimate = PdfBatchSafety.EstimateRasterBytes([pages[i]]);
            if (estimate > maxRasterBytesPerBatch)
                throw new InvalidOperationException(
                    $"Страница {i + 1} превышает бюджет памяти OCR. " +
                    "Уменьшите разрешение или выделите её в отдельный документ.");

            if (count > 0 && (count == maxPagesPerBatch ||
                estimate > maxRasterBytesPerBatch - total))
            {
                batches.Add(new PdfOcrBatch(first, count));
                first = i;
                count = 0;
                total = 0;
            }
            count++;
            total = checked(total + estimate);
        }
        if (count != 0) batches.Add(new PdfOcrBatch(first, count));
        return batches;
    }
}
