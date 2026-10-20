using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GetPdf;

/// <summary>
/// Fail-closed preview/export policy for visible vector page overlays.
/// The first implementation supports only unrotated pages without an explicit CropBox.
/// It does not create or edit PDF /Annots entries and never performs redaction.
/// </summary>
internal static class PdfMarkupPolicy
{
    public static bool CanPlace(PdfPage page) =>
        page.Rotate % 360 == 0 &&
        !page.HasCropBox &&
        !page.Elements.ContainsKey("/CropBox") &&
        !page.Elements.ContainsKey("/UserUnit") &&
        page.MediaBox.X1 == 0 &&
        page.MediaBox.Y1 == 0 &&
        page.MediaBox.Width > 0 &&
        page.MediaBox.Height > 0;

    public static bool[] ReadEligibleSourcePages(string sourcePath)
    {
        using var document = PdfReader.Open(
            Path.GetFullPath(sourcePath), PdfDocumentOpenMode.Import);
        return document.Pages.Cast<PdfPage>().Select(CanPlace).ToArray();
    }

    public static PdfMarkupBox ToPoints(PdfRectangleMarkup rectangle, double width, double height)
    {
        if (rectangle is null || !rectangle.IsValid ||
            !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentException("Недопустимая геометрия разметки PDF.");
        return new PdfMarkupBox(rectangle.X * width, rectangle.Y * height,
            rectangle.Width * width, rectangle.Height * height);
    }
}

internal readonly record struct PdfMarkupBox(double X, double Y, double Width, double Height);
