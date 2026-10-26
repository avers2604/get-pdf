namespace GetDocument.Core.Printing;

/// <summary>
/// Preview pages are the selected PDF source pages, never inferred from the
/// physical sheet numbers returned by CUPS. The print driver is still the
/// authority for margins, output orientation and page imposition.
/// </summary>
public sealed record PrintSheetPreviewLayout(
    int Rows, int Columns, IReadOnlyList<int> PageNumbers,
    int SheetIndex = 0, int TotalSheets = 1);

public static class PrintSheetPreviewPlan
{
    public static PrintSheetPreviewLayout Create(
        int pageCount, PrintPagesPerSheet pagesPerSheet, PrintOrientation orientation)
    {
        ValidateBasic(pageCount, pagesPerSheet, orientation);
        return Create(pageCount, Enumerable.Range(1, pageCount).ToArray(),
            pagesPerSheet, orientation);
    }

    public static PrintSheetPreviewLayout Create(
        int pageCount, IReadOnlyList<int> selectedPages,
        PrintPagesPerSheet pagesPerSheet, PrintOrientation orientation,
        int sheetIndex = 0)
    {
        ValidateBasic(pageCount, pagesPerSheet, orientation);
        ArgumentNullException.ThrowIfNull(selectedPages);
        if (selectedPages.Count is < 1 or > 1000 || selectedPages.Count > pageCount)
            throw new ArgumentException("Выбран недопустимый диапазон страниц.", nameof(selectedPages));
        int previous = 0;
        foreach (int page in selectedPages)
        {
            if (page <= previous || page > pageCount)
                throw new ArgumentException("Страницы должны быть уникальными и возрастающими.",
                    nameof(selectedPages));
            previous = page;
        }

        var (rows, columns) = pagesPerSheet switch
        {
            PrintPagesPerSheet.One => (1, 1),
            PrintPagesPerSheet.Two when orientation == PrintOrientation.Landscape => (1, 2),
            PrintPagesPerSheet.Two => (2, 1),
            PrintPagesPerSheet.Four => (2, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(pagesPerSheet))
        };
        int capacity = checked(rows * columns);
        int totalSheets = (selectedPages.Count + capacity - 1) / capacity;
        if (sheetIndex < 0 || sheetIndex >= totalSheets)
            throw new ArgumentOutOfRangeException(nameof(sheetIndex),
                "Такого листа нет в выбранном диапазоне.");

        var pages = selectedPages.Skip(sheetIndex * capacity).Take(capacity).ToArray();
        return new PrintSheetPreviewLayout(rows, columns,
            Array.AsReadOnly(pages), sheetIndex, totalSheets);
    }

    private static void ValidateBasic(int pageCount, PrintPagesPerSheet pagesPerSheet,
        PrintOrientation orientation)
    {
        if (pageCount is < 1 or > 1000 ||
            !Enum.IsDefined(pagesPerSheet) || !Enum.IsDefined(orientation))
            throw new ArgumentOutOfRangeException(nameof(pageCount));
    }
}
