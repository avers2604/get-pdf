namespace GetPdf;

/// <summary>
/// A bounded preview is not the source for exporting or printing a document.
/// Do not confuse this with a cap on peak memory used internally by PDFium.
/// </summary>
internal static class PdfPreviewBounds
{
    internal const int MaxSide = 1600;
    internal const long MaxPixels = 2_000_000;

    public static (int Width, int Height) Fit(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var pixels = (long)width * height;
        if (width <= MaxSide && height <= MaxSide && pixels <= MaxPixels)
            return (width, height);

        double scale = Math.Min(Math.Min((double)MaxSide / width, (double)MaxSide / height),
            Math.Sqrt((double)MaxPixels / pixels));
        // Floor, rather than round, ensures neither budget can be exceeded.
        return (Math.Max(1, (int)Math.Floor(width * scale)),
                Math.Max(1, (int)Math.Floor(height * scale)));
    }
}
