namespace GetPdf;

/// <summary>
/// Makes a minimal raster import set for an ordered selection of PDF pages.
/// Keeping page references separate preserves duplicates and visible markups
/// without decoding the same source PDF page several times.
/// </summary>
internal sealed record PdfBatchSelection(
    IReadOnlyList<int> SourcePages, IReadOnlyList<PdfPageReference> PreparedPages)
{
    public static PdfBatchSelection Build(IReadOnlyList<PdfPageReference> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count == 0) throw new ArgumentException("Не выбраны страницы.", nameof(selection));
        var originals = new List<int>();
        var lookup = new Dictionary<int, int>();
        var remapped = new List<PdfPageReference>(selection.Count);
        foreach (var page in selection)
        {
            if (page.SourcePage < 0)
                throw new ArgumentOutOfRangeException(nameof(selection), "Неверный номер PDF-страницы.");
            if (!lookup.TryGetValue(page.SourcePage, out int newIndex))
            {
                newIndex = originals.Count;
                lookup.Add(page.SourcePage, newIndex);
                originals.Add(page.SourcePage);
            }
            remapped.Add(page with { SourcePage = newIndex });
        }
        return new PdfBatchSelection(originals.AsReadOnly(), remapped.AsReadOnly());
    }
}
