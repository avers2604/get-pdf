namespace GetDocument.Core.Printing;

/// <summary>
/// Windows printing uses a higher-quality bounded bitmap than interactive PDF
/// preview, with lazy per-page decoding. No uncontrolled 300 DPI allocations.
/// </summary>
public static class PdfPrintRasterPlan
{
    public const int MaxLongSide = 3200;
    public const long MaxPixels = 10_000_000;

    public static (int Width, int Height) GetRasterSize(double pageWidth, double pageHeight)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) ||
            pageWidth <= 0 || pageHeight <= 0 ||
            pageWidth > 20000 || pageHeight > 20000)
            throw new ArgumentOutOfRangeException(nameof(pageWidth));

        double scale = Math.Min(MaxLongSide / Math.Max(pageWidth, pageHeight),
            Math.Sqrt(MaxPixels / (pageWidth * pageHeight)));
        int width = Math.Max(1, (int)Math.Round(pageWidth * scale));
        int height = Math.Max(1, (int)Math.Round(pageHeight * scale));
        while ((long)width * height > MaxPixels)
        {
            if (width >= height) width--;
            else height--;
        }
        return (width, height);
    }
}
