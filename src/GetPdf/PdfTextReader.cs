using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GetPdf;

/// <summary>Extracts searchable text locally, without changing the original PDF.</summary>
internal static class PdfTextReader
{
    public static PdfTextIndex Build(string path, CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(path);
        var sourceTexts = new List<string>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceTexts.Add(ContentOrderTextExtractor.GetText(page) ?? string.Empty);
        }
        return new PdfTextIndex(sourceTexts);
    }
}
