using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GetDocument.Desktop.Printing;

namespace GetDocument.Desktop.Pdf;

/// <summary>
/// PDFium rasterisation for the PDF tab. The document bytes are read once per working
/// copy; every render goes through the shared PDFium gate (one native render at a time
/// across all tabs and the print wizard) and is copied straight into an Avalonia bitmap.
/// </summary>
internal sealed class PdfRaster
{
    private readonly byte[] _bytes;

    public PdfRaster(string path)
    {
        var info = new FileInfo(path);
        GetDocument.Core.Preview.PdfPreviewPlan.ValidateInputLength(info.Length);
        _bytes = File.ReadAllBytes(path);
        PageSizes = PDFtoImage.Conversion.GetPageSizes(_bytes)
            .Select(s => new Size(s.Width, s.Height)).ToArray();
        GetDocument.Core.Preview.PdfPreviewPlan.ValidatePage(0, PageSizes.Count);
    }

    /// <summary>Page sizes in PDF points (1/72 in), in source page order.</summary>
    public IReadOnlyList<Size> PageSizes { get; }

    /// <summary>Renders <paramref name="sourcePage"/> (unrotated) <paramref name="pixelWidth"/> pixels wide.</summary>
    public WriteableBitmap Render(int sourcePage, int pixelWidth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var size = PageSizes[sourcePage];
        pixelWidth = Math.Clamp(pixelWidth, 16, 4000);
        int pixelHeight = Math.Clamp((int)Math.Round(pixelWidth * size.Height / size.Width), 16, 6000);
        PdfNativeGate.Instance.Wait(token);
        try
        {
            token.ThrowIfCancellationRequested();
            using var image = PDFtoImage.Conversion.ToImage(_bytes, page: sourcePage,
                options: new PDFtoImage.RenderOptions(Width: pixelWidth, Height: pixelHeight,
                    WithAnnotations: true, WithFormFill: true));
            var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var target = bitmap.Lock())
            {
                int rowBytes = Math.Min(target.RowBytes, image.RowBytes);
                unsafe
                {
                    byte* from = (byte*)image.GetPixels();
                    byte* to = (byte*)target.Address;
                    for (int y = 0; y < image.Height; y++)
                        Buffer.MemoryCopy(from + y * image.RowBytes, to + y * target.RowBytes, rowBytes, rowBytes);
                }
            }
            return bitmap;
        }
        finally { PdfNativeGate.Instance.Release(); }
    }
}
