using System.Globalization;

namespace GetDocument.Core.Preview;

/// <summary>
/// Pure one-based PDF navigation rules. Treat user input as untrusted and
/// never let an invalid page number reach the native PDF rasterizer.
/// </summary>
public static class PdfPageNavigation
{
    public static bool TryParsePage(string? input, int pageCount, out int zeroBasedIndex)
    {
        zeroBasedIndex = -1;
        if (pageCount is < 1 or > PdfPreviewPlan.MaxPages) return false;
        if (input is null) return false;

        string value = input.Trim();
        if (value.Length is < 1 or > 4 ||
            value.Any(c => c is < '0' or > '9') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int requested) ||
            requested < 1 || requested > pageCount)
            return false;

        zeroBasedIndex = requested - 1;
        return true;
    }

    public static int Move(int index, int offset, int pageCount)
    {
        PdfPreviewPlan.ValidatePage(index, pageCount);
        long destination = (long)index + offset;
        return (int)Math.Clamp(destination, 0, pageCount - 1L);
    }
}
