namespace GetDocument.Core.Printing;

/// <summary>
/// WPF DIPs (1/96 inch). Fit page content inside the validated region that
/// a physical printer says it can image, rather than a fixed guess.
/// </summary>
public sealed record PrintRegion(double X, double Y, double Width, double Height);

public static class PrintImageableArea
{
    public static PrintRegion Choose(double mediaWidth, double mediaHeight,
        double? originX, double? originY, double? printableWidth, double? printableHeight)
    {
        if (!double.IsFinite(mediaWidth) || !double.IsFinite(mediaHeight) ||
            mediaWidth <= 0 || mediaHeight <= 0 ||
            mediaWidth > 20000 || mediaHeight > 20000)
            throw new ArgumentOutOfRangeException(nameof(mediaWidth));

        if (originX is { } x && originY is { } y &&
            printableWidth is { } w && printableHeight is { } h &&
            double.IsFinite(x) && double.IsFinite(y) &&
            double.IsFinite(w) && double.IsFinite(h) &&
            x >= 0 && y >= 0 && w > 0 && h > 0 &&
            x + w <= mediaWidth + 0.5 && y + h <= mediaHeight + 0.5)
            return new PrintRegion(x, y, Math.Min(w, mediaWidth - x),
                Math.Min(h, mediaHeight - y));

        double margin = Math.Min(18, Math.Min(mediaWidth, mediaHeight) / 4);
        return new PrintRegion(margin, margin,
            mediaWidth - 2 * margin, mediaHeight - 2 * margin);
    }
}
