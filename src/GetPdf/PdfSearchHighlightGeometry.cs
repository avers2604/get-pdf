namespace GetPdf;

/// <summary>
/// Normalized, top-left-origin rectangle positioned above the unscaled PDF bitmap.
/// Overlay is visual-only: never a PDF edit, annotation, or secure redaction.
/// </summary>
public sealed record PdfSearchHighlightRect(double X, double Y, double Width, double Height)
{
    public PdfSearchHighlightRect Rotate(int clockwise) => clockwise switch
    {
        0 => this,
        90 => new(1 - Y - Height, X, Height, Width),
        180 => new(1 - X - Width, 1 - Y - Height, Width, Height),
        270 => new(Y, 1 - X - Width, Height, Width),
        _ => throw new ArgumentOutOfRangeException(nameof(clockwise))
    };
}

public static class PdfSearchHighlightGeometry
{
    public const int MaxWords = 2500;
    public const int MaxPhraseWords = 5;

    public static PdfSearchHighlightRect? FromPdfBox(
        double left, double bottom, double width, double height,
        double cropLeft, double cropBottom, double cropWidth, double cropHeight)
    {
        if (new[] {left,bottom,width,height,cropLeft,cropBottom,cropWidth,cropHeight}
            .Any(value => !double.IsFinite(value)) ||
            width <= 0 || height <= 0 || cropWidth <= 0 || cropHeight <= 0)
            return null;

        double x = (left - cropLeft) / cropWidth;
        double y = 1 - (bottom + height - cropBottom) / cropHeight;
        double w = width / cropWidth, h = height / cropHeight;

        // Exact words clipped by a crop box are ambiguous: omit them instead
        // of drawing a plausible but inaccurate highlight in the wrong place.
        const double epsilon = .00001;
        if (x < -epsilon || y < -epsilon || x + w > 1 + epsilon ||
            y + h > 1 + epsilon || w < epsilon || h < epsilon)
            return null;
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        return new PdfSearchHighlightRect(x,y,Math.Min(w,1-x),Math.Min(h,1-y));
    }

    /// <summary>
    /// PdfPig's content-order text and word extractor can have different reading orders.
    /// Return a location only if the query occurs once in BOTH representations and
    /// forms one exact whole-word sequence. Repeated hits are left in the result list
    /// without guessing which word rectangle corresponds to which text occurrence.
    /// </summary>
    public static int UniquePhraseStart(
        IReadOnlyList<string> words, string? query, string? contentOrderText)
    {
        if (words is null || words.Count == 0 || words.Count > MaxWords ||
            string.IsNullOrWhiteSpace(query) || contentOrderText is null ||
            query.Trim().Length > PdfTextIndex.MaxQueryLength)
            return -1;
        string needle = query.Trim();
        var queryWords = needle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (queryWords.Length is < 1 or > MaxPhraseWords) return -1;
        int foundAt = contentOrderText.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (foundAt < 0 || contentOrderText.IndexOf(needle, foundAt + needle.Length,
                StringComparison.OrdinalIgnoreCase) >= 0)
            return -1;
        int candidate = -1;
        for (int i = 0; i + queryWords.Length <= words.Count; i++)
        {
            bool matches = true;
            for (int j = 0; j < queryWords.Length; j++)
                if (!string.Equals(queryWords[j], words[i+j], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            if (!matches) continue;
            if (candidate >= 0) return -1;
            candidate = i;
        }
        return candidate;
    }
}
