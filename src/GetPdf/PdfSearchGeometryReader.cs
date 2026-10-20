using UglyToad.PdfPig;

namespace GetPdf;

/// <summary>
/// Reads only the selected PDF page. Conservative, non-destructive geometry
/// lookup: no guessed bounding boxes if extraction order or page axes differ.
/// </summary>
internal static class PdfSearchGeometryReader
{
    public static IReadOnlyList<PdfSearchHighlightRect> Find(
        string sourcePath, int zeroBasedSourcePage, string? query,
        string sourceText, int editorClockwiseRotation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(sourceText) ||
            editorClockwiseRotation is not (0 or 90 or 180 or 270))
            return Array.Empty<PdfSearchHighlightRect>();

        cancellationToken.ThrowIfCancellationRequested();
        using var pdf = PdfDocument.Open(sourcePath);
        if (zeroBasedSourcePage < 0 || zeroBasedSourcePage >= pdf.NumberOfPages)
            return Array.Empty<PdfSearchHighlightRect>();
        var page = pdf.GetPage(zeroBasedSourcePage + 1);

        // Until PDFium/PdfPig coordinate equivalence is established on real
        // cropped/rotated inputs, never extrapolate a misleading overlay.
        if (page.Rotation.Value != 0)
            return Array.Empty<PdfSearchHighlightRect>();

        var crop = page.CropBox.Bounds;
        double left = Convert.ToDouble(crop.Left), bottom = Convert.ToDouble(crop.Bottom);
        double width = Convert.ToDouble(crop.Width), height = Convert.ToDouble(crop.Height);
        if (width <= 0 || height <= 0 || Math.Abs(left) > .001 || Math.Abs(bottom) > .001 ||
            Math.Abs(Convert.ToDouble(page.Width) - width) > .001 ||
            Math.Abs(Convert.ToDouble(page.Height) - height) > .001)
            return Array.Empty<PdfSearchHighlightRect>();

        // The result list can still show every match. Overlay is conservative:
        // at most one unambiguous phrase (1..5 words), with exact word tokens.
        var words = page.GetWords().Take(PdfSearchHighlightGeometry.MaxWords + 1).ToArray();
        if (words.Length > PdfSearchHighlightGeometry.MaxWords) return Array.Empty<PdfSearchHighlightRect>();
        int start = PdfSearchHighlightGeometry.UniquePhraseStart(
            words.Select(word => word.Text).ToArray(), query, sourceText);
        if (start < 0) return Array.Empty<PdfSearchHighlightRect>();
        int count = query.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var bounds = new List<PdfSearchHighlightRect>(count);
        for (int i = start; i < start + count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (words[i].TextOrientation != UglyToad.PdfPig.Content.TextOrientation.Horizontal)
                return Array.Empty<PdfSearchHighlightRect>();
            var box = words[i].BoundingBox;
            var rect = PdfSearchHighlightGeometry.FromPdfBox(
                Convert.ToDouble(box.Left), Convert.ToDouble(box.Bottom),
                Convert.ToDouble(box.Width), Convert.ToDouble(box.Height),
                left, bottom, width, height);
            if (rect is null) return Array.Empty<PdfSearchHighlightRect>();
            bounds.Add(rect.Rotate(editorClockwiseRotation));
        }
        return bounds;
    }
}
