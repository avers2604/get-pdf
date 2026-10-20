namespace GetPdf;

/// <summary>
/// Builds a non-destructive copy plan from two *unchanged* open PDF documents.
/// Any visible annotations, source revisions or page rotations in the plans are
/// refused rather than silently dropped when writing a mixed-source PDF.
/// </summary>
internal sealed record PdfCrossTabSnapshot(string WorkingPath, string OriginalPath,
    PdfPageReference[] Pages, int SelectedPageIndex, int SourcePageCount);

internal sealed record PdfTabPageDrag(PdfViewerWindow Source, Guid PageId);

internal static class PdfCrossTabPageTransfer
{
    internal static int FindPageById(IReadOnlyList<PdfPageReference> pages, Guid id)
    {
        ArgumentNullException.ThrowIfNull(pages);
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Id == id) return i;
        return -1;
    }

    internal static PdfSourcePageReference[] Build(
        string destinationSource,
        IReadOnlyList<PdfPageReference> destinationPages,
        int insertAfter,
        int destinationSourcePageCount,
        string copiedFromSource,
        PdfPageReference copiedPage,
        int copiedSourcePageCount,
        int maxPages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(copiedFromSource);
        ArgumentNullException.ThrowIfNull(destinationPages);
        ArgumentNullException.ThrowIfNull(copiedPage);
        if (maxPages < 2)
            throw new ArgumentOutOfRangeException(nameof(maxPages));
        if (destinationSourcePageCount < 1 || copiedSourcePageCount < 1)
            throw new ArgumentOutOfRangeException(nameof(destinationSourcePageCount));
        if (destinationPages.Count < 1 || destinationPages.Count >= maxPages)
            throw new InvalidOperationException("Превышен лимит страниц итогового PDF.");
        if (insertAfter < 0 || insertAfter >= destinationPages.Count)
            throw new ArgumentOutOfRangeException(nameof(insertAfter));

        string target = Path.GetFullPath(destinationSource);
        string source = Path.GetFullPath(copiedFromSource);
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите два разных документа PDF.");

        static void CheckPage(PdfPageReference page, int sourceCount)
        {
            if (page.SourcePage < 0 || page.SourcePage >= sourceCount)
                throw new ArgumentOutOfRangeException(nameof(page), "Некорректная исходная страница PDF.");
            if (page.Rotation != 0 || !string.IsNullOrEmpty(page.Stamp) ||
                page.Rectangles is { Count: > 0 } || page.Notes is { Count: > 0 })
                throw new InvalidOperationException(
                    "Сначала сохраните редакторские изменения в отдельный PDF и откройте его снова.");
        }

        foreach (var page in destinationPages)
        {
            ArgumentNullException.ThrowIfNull(page);
            CheckPage(page, destinationSourcePageCount);
        }
        CheckPage(copiedPage, copiedSourcePageCount);

        var result = new List<PdfSourcePageReference>(destinationPages.Count + 1);
        for (int i = 0; i < destinationPages.Count; i++)
        {
            result.Add(new PdfSourcePageReference(target, destinationPages[i].SourcePage));
            if (i == insertAfter)
                result.Add(new PdfSourcePageReference(source, copiedPage.SourcePage));
        }
        return result.ToArray();
    }
}
