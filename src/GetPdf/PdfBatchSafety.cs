namespace GetPdf;

/// <summary>A page size in PDF points (1 point = 1/72 inch).</summary>
internal readonly record struct PdfPageDimensions(double WidthPt, double HeightPt);

/// <summary>
/// Conservative preflight for legacy full-document NAPS2 PDF raster operations.
/// This reduces predictable OOM cases but is NOT a hard runtime memory limit.
/// Switch to a streaming backend before claiming unlimited-page OCR/printing.
/// </summary>
internal static class PdfBatchSafety
{
    private const long MiB = 1024L * 1024;
    private const long MaxBudget = 1024L * MiB;
    private const long FallbackBudget = 512L * MiB;

    public static long EffectiveBudget()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? Math.Min(available / 3, MaxBudget) : FallbackBudget;
    }

    public static long EstimateRasterBytes(IEnumerable<PdfPageDimensions> pages, int dpi = 300)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (dpi is < 72 or > 1200) throw new ArgumentOutOfRangeException(nameof(dpi));
        long bytes = 0;
        int count = 0;
        foreach (var page in pages)
        {
            count++;
            if (!double.IsFinite(page.WidthPt) || !double.IsFinite(page.HeightPt) ||
                page.WidthPt < 1 || page.HeightPt < 1 ||
                page.WidthPt > 14400 || page.HeightPt > 14400)
                throw new InvalidDataException("Размер страницы PDF некорректен или слишком велик.");

            // Decoded BGRA pixel buffer + one approximate working copy/transform.
            long width = checked((long)Math.Ceiling(page.WidthPt * dpi / 72.0));
            long height = checked((long)Math.Ceiling(page.HeightPt * dpi / 72.0));
            try { bytes = checked(bytes + checked(width * height * 8)); }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("Оценочный объём растрового PDF слишком велик.", ex);
            }
        }
        if (count == 0) throw new InvalidDataException("PDF не содержит страниц.");
        return bytes;
    }

    public static void EnsureWithinBudget(long estimatedBytes, long memoryBudgetBytes)
    {
        if (memoryBudgetBytes < 1) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
        if (estimatedBytes < 0) throw new ArgumentOutOfRangeException(nameof(estimatedBytes));
        if (estimatedBytes > memoryBudgetBytes)
        {
            long estimatedMiB = (estimatedBytes + MiB - 1) / MiB;
            long budgetMiB = memoryBudgetBytes / MiB;
            throw new InvalidOperationException(
                $"Пакетная загрузка PDF требует ориентировочно {estimatedMiB} МиБ " +
                $"при доступном бюджете {budgetMiB} МиБ. " +
                "Для печати или OCR сначала извлеките небольшой диапазон страниц в отдельный PDF. " +
                "Оригинал не изменён.");
        }
    }
}
