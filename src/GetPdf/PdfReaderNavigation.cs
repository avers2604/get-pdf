using System.Globalization;

namespace GetPdf;

/// <summary>Pure, bounds-checked reader navigation shared by shortcuts and controls.</summary>
internal static class PdfReaderNavigation
{
    internal static bool TryParsePage(string? input, int pageCount, out int index)
    {
        index = -1;
        if (pageCount <= 0 || string.IsNullOrWhiteSpace(input)) return false;
        string value = input.Trim();
        if (value.Length > 7 || value.Any(c => c < '0' || c > '9')) return false;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int page) ||
            page < 1 || page > pageCount) return false;
        index = page - 1;
        return true;
    }

    internal static int PreviousSearchHit(int current, int count)
    {
        if (count <= 0) return -1;
        return ((current % count) + count - 1) % count;
    }
}
