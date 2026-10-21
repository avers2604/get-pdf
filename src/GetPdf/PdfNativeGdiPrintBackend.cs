using System.ComponentModel;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;

namespace GetPdf;

/// <summary>
/// Experimental PDFium Windows HDC path. Draws native PDF page operations into
/// a printer device context instead of always sending an image from the WPF
/// paginator. Native GDI/driver fallback can still rasterize some content.
/// Do not assert guaranteed vector spool output without physical verification.
/// </summary>
internal static class PdfNativeGdiPrintBackend
{
    private const int FPDF_ANNOT = 0x01;
    private const int FPDF_RENDER_LIMITEDIMAGECACHE = 0x200;
    private const int FPDF_PRINTING = 0x800;
    private const int HORZRES = 8;
    private const int VERTRES = 10;
    private const int LOGPIXELSX = 88;
    private const int LOGPIXELSY = 90;

    // Pdfium native assets come from the existing NAPS2.Pdfium.Binaries
    // runtime, no separate downloadable component or network connection.
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_InitLibrary();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadDocument(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fileName, IntPtr password);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPage(
        IntPtr dc, IntPtr page, int x, int y, int w, int h, int rotate, int flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDCW",
        SetLastError = true)]
    private static extern IntPtr CreatePrinterDc(
        string driver, string device, string? output, IntPtr devMode);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int StartDoc(IntPtr hdc, ref NativeDocInfo info);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndDoc(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int AbortDoc(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StartPage(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndPage(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDocInfo
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public IntPtr Output;
        public IntPtr DataType;
        public int Type;
    }

    private static readonly Lazy<bool> Initialized = new(() =>
    {
        FPDF_InitLibrary();
        return true;
    });

    internal static bool IsSafeSourceSelection(IReadOnlyList<PdfPageReference> pages)
    {
        if (pages.Count is < 1 or > 1000) return false;
        return pages.All(p => p is not null && p.Rotation == 0 &&
            string.IsNullOrEmpty(p.Stamp) &&
            p.Rectangles is not { Count: > 0 } &&
            p.Notes is not { Count: > 0 });
    }

    internal static void Print(
        string source, IReadOnlyList<PdfPageReference> pages,
        string printerName, PrintTicket printTicket, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PDFium/GDI печать поддерживается только Windows.");
        if (!WorkstationPolicy.Current.AllowPrinting ||
            !WorkstationPolicy.Current.AllowNativeGdiPrinting)
            throw new InvalidOperationException("Нативная печать отключена корпоративной политикой.");
        if (!IsSafeSourceSelection(pages))
            throw new InvalidOperationException("Для нативной печати требуются страницы без редакторских наложений и поворотов.");
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentNullException.ThrowIfNull(printTicket);
        token.ThrowIfCancellationRequested();
        // The experimental GDI backend must not run concurrently with
        // the native import workers used by other opened PDF tabs.
        using var exclusive = PdfRenderConcurrency.EnterExclusiveAsync(token)
            .GetAwaiter().GetResult();

        // Convert the exact selected PrintTicket to printer DEVMODE; otherwise
        // A3/A4/duplex/copy settings would be silently dropped by CreateDC.
        using var converter = new PrintTicketConverter(printerName, 1);
        byte[] devMode = converter.ConvertPrintTicketToDevMode(
            printTicket, BaseDevModeType.PrinterDefault, PrintTicketScope.JobScope);
        IntPtr devPtr = Marshal.AllocHGlobal(devMode.Length);
        IntPtr dc = IntPtr.Zero, pdf = IntPtr.Zero;
        bool submitted = false, started = false;
        try
        {
            Marshal.Copy(devMode, 0, devPtr, devMode.Length);
            dc = CreatePrinterDc("WINSPOOL", printerName, null, devPtr);
            if (dc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Не удалось создать контекст устройства принтера.");
            _ = Initialized.Value;
            pdf = FPDF_LoadDocument(Path.GetFullPath(source), IntPtr.Zero);
            if (pdf == IntPtr.Zero)
                throw new InvalidDataException("PDFium не смог открыть рабочий PDF для печати.");
            int total = FPDF_GetPageCount(pdf);
            if (total < 1 || pages.Any(p => p.SourcePage < 0 || p.SourcePage >= total))
                throw new InvalidDataException("План страниц не соответствует PDF.");
            int w = GetDeviceCaps(dc, HORZRES), h = GetDeviceCaps(dc, VERTRES);
            int dpiX = GetDeviceCaps(dc, LOGPIXELSX), dpiY = GetDeviceCaps(dc, LOGPIXELSY);
            if (w < 1 || h < 1 || dpiX < 1 || dpiY < 1)
                throw new InvalidDataException("Драйвер сообщил некорректную область печати.");

            token.ThrowIfCancellationRequested();
            var info = new NativeDocInfo
            {
                cbSize = Marshal.SizeOf<NativeDocInfo>(),
                Name = "ГЭТ PDF — нативная печать"
            };
            if (StartDoc(dc, ref info) <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Служба печати отклонила документ.");
            started = true;

            foreach (var planned in pages)
            {
                token.ThrowIfCancellationRequested();
                IntPtr page = FPDF_LoadPage(pdf, planned.SourcePage);
                if (page == IntPtr.Zero)
                    throw new InvalidDataException("PDFium не смог загрузить страницу печати.");
                try
                {
                    float pageWidth = FPDF_GetPageWidthF(page);
                    float pageHeight = FPDF_GetPageHeightF(page);
                    // 1/12 inch safety margin in BOTH physical axes.
                    // Non-square printer resolutions are supported by drivers.
                    var rectangle = PdfNativeGdiPrintLayout.Fit(pageWidth, pageHeight,
                        w, h, Math.Max(0, dpiX / 12), Math.Max(0, dpiY / 12),
                        dpiX, dpiY);
                    if (StartPage(dc) <= 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Драйвер не смог начать страницу.");
                    FPDF_RenderPage(dc, page, rectangle.X, rectangle.Y,
                        rectangle.Width, rectangle.Height, 0,
                        FPDF_ANNOT | FPDF_PRINTING | FPDF_RENDER_LIMITEDIMAGECACHE);
                    if (EndPage(dc) <= 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Драйвер не смог закончить страницу.");
                }
                finally { FPDF_ClosePage(page); }
            }
            token.ThrowIfCancellationRequested();
            if (EndDoc(dc) <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Задание печати не принято.");
            submitted = true;
        }
        finally
        {
            if (started && !submitted && dc != IntPtr.Zero)
                AbortDoc(dc);
            if (pdf != IntPtr.Zero) FPDF_CloseDocument(pdf);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            Marshal.FreeHGlobal(devPtr);
        }
    }
}

internal readonly record struct PdfNativeGdiRect(int X, int Y, int Width, int Height);

internal static class PdfNativeGdiPrintLayout
{
    internal static PdfNativeGdiRect Fit(
        double pageWidth, double pageHeight, int printableWidth,
        int printableHeight, int marginXPixels, int marginYPixels,
        int dpiX, int dpiY)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) ||
            pageWidth <= 0 || pageHeight <= 0 || printableWidth < 1 ||
            printableHeight < 1 || marginXPixels < 0 || marginYPixels < 0 ||
            dpiX < 1 || dpiY < 1)
            throw new ArgumentOutOfRangeException(nameof(pageWidth));
        // Fail closed instead of silently discarding user-requested margins
        // when the chosen paper/driver reports a very small printable area.
        if (marginXPixels * 2L >= printableWidth ||
            marginYPixels * 2L >= printableHeight)
            throw new ArgumentOutOfRangeException(nameof(marginXPixels),
                "Поля больше доступной области печати.");
        double usableW = printableWidth - marginXPixels * 2.0;
        double usableH = printableHeight - marginYPixels * 2.0;
        // FPDF_RenderPage takes device PIXELS, not physical dimensions.
        // Non-square device resolutions require width/height in pixels to be
        // proportional to (PDF width * dpiX) / (PDF height * dpiY).
        // Otherwise A4 is stretched on e.g. 600x300 DPI print devices.
        double aspect = (pageWidth / pageHeight) * ((double)dpiX / dpiY);
        if (!double.IsFinite(aspect) || aspect <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageWidth),
                "Невозможно определить пропорции PDF-страницы.");
        double w = Math.Min(usableW, usableH * aspect);
        double h = w / aspect;
        int width = Math.Clamp((int)Math.Round(w), 1, printableWidth);
        int height = Math.Clamp((int)Math.Round(h), 1, printableHeight);
        return new PdfNativeGdiRect((printableWidth - width) / 2,
            (printableHeight - height) / 2, width, height);
    }
}
