using System.Globalization;

namespace GetPdf;

/// <summary>
/// Validates 1-based page numbers and ranges against the CURRENT visible order.
/// Input order is preserved; duplicates are rejected to avoid accidental repeated pages.
/// </summary>
public static class PdfPageRange
{
    public static IReadOnlyList<PdfPageReference> Parse(
        string? spec, IReadOnlyList<PdfPageReference> visiblePages)
    {
        ArgumentNullException.ThrowIfNull(visiblePages);
        if (string.IsNullOrWhiteSpace(spec) || spec.Length > 4096)
            throw new FormatException("Укажите номера страниц, например: 1-3, 5, 7-9.");

        var indices = new List<int>();
        var seen = new HashSet<int>();
        foreach (string token in spec.Split(','))
        {
            var part = token.Trim();
            if (part.Length == 0)
                throw new FormatException("Пустой номер страницы.");
            var bounds = part.Split('-');
            if (bounds.Length is < 1 or > 2 ||
                !int.TryParse(bounds[0].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int first))
                throw new FormatException($"Недопустимый диапазон: {part}.");
            int last = first;
            if (bounds.Length == 2 &&
                !int.TryParse(bounds[1].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out last))
                throw new FormatException($"Недопустимый диапазон: {part}.");
            if (first < 1 || last < first || last > visiblePages.Count)
                throw new FormatException($"Номер страницы выходит за пределы 1–{visiblePages.Count}: {part}.");
            for (int number = first; number <= last; number++)
            {
                int index = number - 1;
                if (!seen.Add(index))
                    throw new FormatException($"Страница {number} указана повторно.");
                indices.Add(index);
            }
        }
        return indices.Select(index => visiblePages[index]).ToArray();
    }
}
