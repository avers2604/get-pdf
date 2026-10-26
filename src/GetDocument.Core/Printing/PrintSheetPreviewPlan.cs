namespace GetDocument.Core.Printing;

/// <summary>
/// Layout of actual first document pages used for GET's preliminary sheet
/// preview. PrintTicket, CUPS PPD and imageable-area margins still govern the
/// physical output; this is an honest approximate sheet composition only.
/// </summary>
public sealed record PrintSheetPreviewLayout(
    int Rows, int Columns, IReadOnlyList<int> PageNumbers);

public static class PrintSheetPreviewPlan
{
    public static PrintSheetPreviewLayout Create(
        int pageCount, PrintPagesPerSheet pagesPerSheet, PrintOrientation orientation)
    {
        if (pageCount is < 1 or > 1000 ||
            !Enum.IsDefined(pagesPerSheet) || !Enum.IsDefined(orientation))
            throw new ArgumentOutOfRangeException(nameof(pageCount));

        var (rows, columns) = pagesPerSheet switch
        {
            PrintPagesPerSheet.One => (1, 1),
            PrintPagesPerSheet.Two when orientation == PrintOrientation.Landscape => (1, 2),
            PrintPagesPerSheet.Two => (2, 1),
            PrintPagesPerSheet.Four => (2, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(pagesPerSheet))
        };
        return new PrintSheetPreviewLayout(rows, columns,
            Array.AsReadOnly(Enumerable.Range(1, Math.Min(pageCount, rows * columns)).ToArray()));
    }
}
