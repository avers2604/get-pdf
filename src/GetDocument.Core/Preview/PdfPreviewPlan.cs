namespace GetDocument.Core.Preview;

/// <summary>Conservative resource limits for an in-process PDF thumbnail/reading preview.</summary>
public static class PdfPreviewPlan
{
    public const long MaxInputBytes = 128L * 1024 * 1024;
    public const int MaxPages = 1000;
    public const int DefaultLongSide = 1440;

    public static void ValidateInputLength(long length)
    {
        if (length is <= 0 or > MaxInputBytes)
            throw new InvalidDataException("PDF пуст или превышает лимит просмотра 128 МиБ.");
    }

    public static void ValidatePage(int index, int pageCount)
    {
        if (pageCount is < 1 or > MaxPages)
            throw new InvalidDataException("PDF не имеет страниц либо превышает лимит 1000 страниц.");
        if (index < 0 || index >= pageCount)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>
    /// Bounds each rasterized page by one axis and checks PDF page points for overflow/NaN.
    /// Native decoders additionally need sandboxing and process memory limits before release.
    /// </summary>
    public static (int Width, int Height) GetRasterSize(
        double pageWidth, double pageHeight, int maxLongSide = DefaultLongSide)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) ||
            pageWidth <= 0 || pageHeight <= 0 || pageWidth > 20000 || pageHeight > 20000)
            throw new ArgumentOutOfRangeException(nameof(pageWidth));
        if (maxLongSide is < 320 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(maxLongSide));
        double scale = Math.Min(4.0, maxLongSide / Math.Max(pageWidth, pageHeight));
        return (Math.Clamp((int)Math.Round(pageWidth * scale), 1, maxLongSide),
                Math.Clamp((int)Math.Round(pageHeight * scale), 1, maxLongSide));
    }
}
