using GetDocument.Core.Preview;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GetDocument.Desktop.Preview;

/// <summary>
/// Reads the existing PDF text layer with PdfPig (already used by legacy GetPdf).
/// Never creates an index on disk and does not store full extracted page text.
/// </summary>
internal static class PdfTextSearchService
{
    public static PdfTextSearchResults Search(
        string path, string normalizedQuery, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        normalizedQuery = PdfTextSearchPlan.NormalizeQuery(normalizedQuery);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.SequentialScan);
        PdfPreviewPlan.ValidateInputLength(stream.Length);

        using var document = PdfDocument.Open(stream);
        int pages = document.NumberOfPages;
        PdfPreviewPlan.ValidatePage(0, pages);

        var matches = new List<PdfTextPageMatch>();
        int withoutTextLayer = 0;
        int truncated = 0;
        for (int page = 0; page < pages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Use content-order extraction; raw Page.Text may be out of reading order.
            string text = ContentOrderTextExtractor.GetText(document.GetPage(page + 1));
            if (string.IsNullOrWhiteSpace(text))
            {
                withoutTextLayer++;
                continue;
            }
            if (text.Length > PdfTextSearchPlan.MaxCharactersPerPage)
                truncated++;
            PdfTextPageMatch? hit = PdfTextSearchPlan.FindInPage(page, text, normalizedQuery);
            if (hit is not null)
            {
                if (hit.IsPartial && text.Length <= PdfTextSearchPlan.MaxCharactersPerPage)
                    truncated++;
                matches.Add(hit);
            }
        }

        return new PdfTextSearchResults(matches, pages, withoutTextLayer, truncated);
    }
}

internal sealed record PdfTextSearchResults(
    IReadOnlyList<PdfTextPageMatch> Pages, int InspectedPages,
    int PagesWithoutTextLayer, int PartialPages);
