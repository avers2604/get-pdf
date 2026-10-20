using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAPS2.Images;
using NAPS2.Images.Wpf;

namespace GetPdf;

/// <summary>
/// Uses the existing NAPS2/PDFium page decoder. Output bitmap is small and frozen;
/// PDFium may still temporarily allocate an entire decoded source page.
/// This path NEVER supplies pixels to printing or PDF export.
/// </summary>
internal static class PdfThumbnailRenderer
{
    public static BitmapSource Render(ProcessedImage image, int clockwiseRotation)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (clockwiseRotation is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(clockwiseRotation));

        var full = image.RenderToBitmapSource();
        BitmapSource oriented = clockwiseRotation == 0
            ? full : new TransformedBitmap(full, new RotateTransform(clockwiseRotation));
        var (width,height)=PdfThumbnailBounds.Fit(oriented.PixelWidth, oriented.PixelHeight);
        BitmapSource reduced = width == oriented.PixelWidth && height == oriented.PixelHeight
            ? oriented : new TransformedBitmap(oriented,
                new ScaleTransform((double)width / oriented.PixelWidth,
                    (double)height / oriented.PixelHeight));
        var bgra=new FormatConvertedBitmap(reduced,PixelFormats.Bgra32,null,0);
        int stride=checked(bgra.PixelWidth * 4);
        byte[] bytes=new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(bytes,stride,0);
        var thumbnail=BitmapSource.Create(bgra.PixelWidth,bgra.PixelHeight,
            bgra.DpiX,bgra.DpiY,PixelFormats.Bgra32,null,bytes,stride);
        thumbnail.Freeze();
        return thumbnail;
    }
}
