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

    /// <summary>
    /// True when the page and the paper have different orientation (landscape page,
    /// portrait sheet or the reverse). Square or near-square pages are never turned.
    /// </summary>
    public static bool ShouldRotate(double pageWidth, double pageHeight, double paperWidth, double paperHeight)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) ||
            !double.IsFinite(paperWidth) || !double.IsFinite(paperHeight) ||
            pageWidth <= 0 || pageHeight <= 0 || paperWidth <= 0 || paperHeight <= 0)
            return false;
        const double squareTolerance = 1.02;
        bool pageLandscape = pageWidth > pageHeight * squareTolerance;
        bool pagePortrait = pageHeight > pageWidth * squareTolerance;
        bool paperLandscape = paperWidth > paperHeight;
        return paperLandscape ? pagePortrait : pageLandscape;
    }
}

/// <summary>Platform-neutral check of what an installed Windows print driver advertises.</summary>
public static class PrintCapabilityPolicy
{
    /// <param name="advertised">Driver's duplexing list as names ("OneSided", "TwoSidedLongEdge"…).</param>
    /// <param name="requested">Requested mode name.</param>
    public static bool SupportsDuplexing(IReadOnlyCollection<string> advertised, string requested)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        // An empty list means the driver has no duplex unit: one-sided only.
        if (advertised.Count == 0) return requested == "OneSided";
        return advertised.Contains(requested, StringComparer.Ordinal);
    }
}
