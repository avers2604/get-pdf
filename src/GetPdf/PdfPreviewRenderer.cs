using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAPS2.Images;
using NAPS2.Images.Wpf;

namespace GetPdf;

/// <summary>
/// Keeps the cached WPF screen preview below a fixed number of pixels.
/// Only renders the currently selected page. Export and printing use originals.
/// NOTE: NAPS2/PDFium can still temporarily allocate a full-resolution page.
/// </summary>
internal static class PdfPreviewRenderer
{
    public static BitmapSource RenderBounded(ProcessedImage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var source = page.RenderToBitmapSource();
        var (width, height) = PdfPreviewBounds.Fit(source.PixelWidth, source.PixelHeight);
        if (width == source.PixelWidth && height == source.PixelHeight)
        {
            if (source.CanFreeze) source.Freeze();
            return source;
        }
        // The independent frozen copy below lets the large WPF image be collected.
        var scaled = new TransformedBitmap(source,
            new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        var preview = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        preview.Freeze();
        return preview;
    }
}
