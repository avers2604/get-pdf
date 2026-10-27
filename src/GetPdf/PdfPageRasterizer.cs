using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GetPdf;

/// <summary>
/// Renders one PDF page straight to the requested screen size with PDFium.
/// The NAPS2 path decoded every page at 300 dpi first (a full A0 sheet is ~140 Mpx,
/// ~560 MB per copy) and only then scaled it down, which froze or killed the viewer
/// on large-format drawings and posters. Here PDFium allocates only the target bitmap.
/// Not used for printing or export: those keep reading the original PDF.
/// </summary>
internal static class PdfPageRasterizer
{
    /// <summary>Nominal raster density the preview bounds were designed against.</summary>
    internal const double NominalDpi = 300;

    private const int FPDF_ANNOT = 0x01;
    private const int FPDF_LCD_TEXT = 0x02;
    private const int FPDF_RENDER_LIMITEDIMAGECACHE = 0x200;
    private const int FPDFBitmap_BGRA = 4;

    // Same NAPS2.Pdfium.Binaries native library as the import and print paths.
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_InitLibrary();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadDocument(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fileName, IntPtr password);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPageBitmap(
        IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    private static readonly Lazy<bool> Initialized = new(() =>
    {
        FPDF_InitLibrary();
        return true;
    });

    /// <summary>
    /// Pixel size of a page of <paramref name="widthPt"/>×<paramref name="heightPt"/> points at the
    /// nominal 300 dpi, reduced by <paramref name="fit"/> (preview or thumbnail bounds).
    /// </summary>
    internal static (int Width, int Height) TargetSize(double widthPt, double heightPt,
        Func<int, int, (int Width, int Height)> fit)
    {
        if (!double.IsFinite(widthPt) || !double.IsFinite(heightPt) || widthPt <= 0 || heightPt <= 0)
            throw new InvalidDataException("Размер страницы PDF некорректен.");
        // PDF caps a page at 14 400 pt (200 in), so the nominal raster stays well inside int.
        int width = (int)Math.Clamp(Math.Ceiling(widthPt * NominalDpi / 72), 1, int.MaxValue / 8);
        int height = (int)Math.Clamp(Math.Ceiling(heightPt * NominalDpi / 72), 1, int.MaxValue / 8);
        return fit(width, height);
    }

    /// <summary>
    /// Caller must hold <see cref="PdfRenderConcurrency.EnterExclusiveAsync"/>: PDFium is not
    /// thread-safe and NAPS2 drives the same native library from other tabs.
    /// </summary>
    internal static BitmapSource Render(string path, int pageIndex,
        Func<int, int, (int Width, int Height)> fit)
    {
        _ = Initialized.Value;
        IntPtr document = FPDF_LoadDocument(Path.GetFullPath(path), IntPtr.Zero);
        if (document == IntPtr.Zero)
            throw new InvalidDataException("PDFium не смог открыть PDF для просмотра.");
        try
        {
            IntPtr page = FPDF_LoadPage(document, pageIndex);
            if (page == IntPtr.Zero)
                throw new InvalidDataException("PDFium не смог загрузить страницу для просмотра.");
            try
            {
                // Width/height already include the page's /Rotate, like the rendered bitmap.
                var (width, height) = TargetSize(FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page), fit);
                int stride = checked(width * 4);
                var pixels = new byte[checked(stride * height)];
                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    IntPtr bitmap = FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRA, pin.AddrOfPinnedObject(), stride);
                    if (bitmap == IntPtr.Zero)
                        throw new InvalidDataException("PDFium не смог выделить изображение страницы.");
                    try
                    {
                        FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0,
                            FPDF_ANNOT | FPDF_LCD_TEXT | FPDF_RENDER_LIMITEDIMAGECACHE);
                    }
                    finally { FPDFBitmap_Destroy(bitmap); }
                }
                finally { pin.Free(); }
                // Same DPI the NAPS2 raster carried: page overlays and highlight canvases size from it.
                var source = BitmapSource.Create(width, height, NominalDpi, NominalDpi, PixelFormats.Bgra32, null, pixels, stride);
                source.Freeze();
                return source;
            }
            finally { FPDF_ClosePage(page); }
        }
        finally { FPDF_CloseDocument(document); }
    }
}
