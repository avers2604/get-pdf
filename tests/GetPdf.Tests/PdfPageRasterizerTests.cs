using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfPageRasterizerTests
{
    [TestMethod]
    public void A4PreviewKeepsThePreviousOnScreenSize()
    {
        // 2480×3508 px at 300 dpi, reduced by the preview bounds exactly as before.
        Assert.AreEqual(PdfPreviewBounds.Fit(2480, 3508), PdfPageRasterizer.TargetSize(595.2, 841.8, PdfPreviewBounds.Fit));
    }

    [TestMethod]
    public void LargeFormatSheetsStayWithinPreviewAndThumbnailBounds()
    {
        foreach (var (w, h) in new[] { (2384.0, 3370.0), (14400.0, 7200.0), (14400.0, 14400.0) })
        {
            var preview = PdfPageRasterizer.TargetSize(w, h, PdfPreviewBounds.Fit);
            Assert.IsTrue(preview.Width <= PdfPreviewBounds.MaxSide && preview.Height <= PdfPreviewBounds.MaxSide);
            Assert.IsTrue((long)preview.Width * preview.Height <= PdfPreviewBounds.MaxPixels);
            Assert.AreEqual(w / h, (double)preview.Width / preview.Height, 0.01);
            var thumb = PdfPageRasterizer.TargetSize(w, h, PdfThumbnailBounds.Fit);
            Assert.IsTrue((long)thumb.Width * thumb.Height <= PdfThumbnailBounds.MaxPixels);
        }
        Assert.ThrowsExactly<InvalidDataException>(() => PdfPageRasterizer.TargetSize(0, 842, PdfPreviewBounds.Fit));
    }

    [TestMethod]
    public void RendersAnA0PageDirectlyAtPreviewSize()
    {
        string path = Path.Combine(Path.GetTempPath(), "getpdf-a0-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, SinglePagePdf(2384, 3370,
            "0.9 0.1 0.2 rg 40 40 2304 3290 re f"));
        try
        {
            BitmapSource page = PdfPageRasterizer.Render(path, 0, PdfPreviewBounds.Fit);
            Assert.AreEqual(PdfPageRasterizer.TargetSize(2384, 3370, PdfPreviewBounds.Fit), (page.PixelWidth, page.PixelHeight));
            Assert.IsTrue(page.IsFrozen);
            var (r, g, b) = Pixel(page, page.PixelWidth / 2, page.PixelHeight / 2);
            Assert.IsTrue(r > 200 && g < 60 && b < 90, $"centre {r},{g},{b} is not the red fill");
            (r, g, b) = Pixel(page, 1, 1);
            Assert.IsTrue(r > 240 && g > 240 && b > 240, $"margin {r},{g},{b} is not white paper");
            string? dump = Environment.GetEnvironmentVariable("GETPDF_RASTER_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(page));
                using var file = File.Create(dump);
                png.Save(file);
            }
        }
        finally { File.Delete(path); }
    }

    private static (byte R, byte G, byte B) Pixel(BitmapSource bitmap, int x, int y)
    {
        var bgra = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), bgra, 4, 0);
        return (bgra[2], bgra[1], bgra[0]);
    }

    private static byte[] SinglePagePdf(double width, double height, string content)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R >>"),
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
