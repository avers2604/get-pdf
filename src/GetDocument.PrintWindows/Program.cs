using System.IO;
using System.Printing;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using GetDocument.Core.Printing;
using GetDocument.Core.Preview;
using PDFtoImage;

namespace GetDocument.PrintWindows;

/// <summary>
/// Headless native print worker, launched only by the GET-owned wizard.
/// No PrintDialog, visible window, internet or shell. Runs on Windows STA.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) return 2;
            using var server = new LocalPrintServer();
            if (args[0] == "printers")
            {
                var queues = server.GetPrintQueues()
                    .Select(q => q.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                Console.Write(JsonSerializer.Serialize(queues));
                return 0;
            }
            if (args[0] != "print") return 2;
            char[] input = new char[8193];
            int length = 0;
            while (length < input.Length)
            {
                int read = Console.In.Read(input, length, input.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length == input.Length) return 2;
            var request = JsonSerializer.Deserialize<PrintRequest>(new string(input, 0, length))
                ?? throw new InvalidDataException("Отсутствуют настройки печати.");
            PrintJobPlan plan = PrintJobPlan.Create(request.PrinterName,
                request.SourcePageCount, request.PageRange, request.Copies,
                request.Duplex, request.Paper);
            if (string.IsNullOrWhiteSpace(request.PdfPath) ||
                !Path.IsPathFullyQualified(request.PdfPath) ||
                !request.PdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return 2;

            using var source = new FileStream(request.PdfPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.SequentialScan);
            PdfPreviewPlan.ValidateInputLength(source.Length);
            int actualPages = Conversion.GetPageCount(source, leaveOpen: true);
            if (actualPages != plan.SourcePageCount) return 3;
            source.Position = 0;

            var names = server.GetPrintQueues().Select(q => q.Name).ToArray();
            if (!names.Contains(plan.PrinterName, StringComparer.Ordinal)) return 3;
            using var queue = server.GetPrintQueue(plan.PrinterName);
            var capabilities = queue.GetPrintCapabilities();
            Duplexing duplex = plan.Duplex switch
            {
                PrintDuplex.LongEdge => Duplexing.TwoSidedLongEdge,
                PrintDuplex.ShortEdge => Duplexing.TwoSidedShortEdge,
                _ => Duplexing.OneSided
            };
            PageMediaSizeName paper = plan.Paper == PrintPaper.A3
                ? PageMediaSizeName.ISOA3 : PageMediaSizeName.ISOA4;
            // Drivers without a duplex unit (Microsoft Print to PDF, XPS Writer, many
            // single-sided printers) report an EMPTY duplexing list: one-sided is still
            // the only thing they do. Two-sided must be advertised explicitly.
            bool duplexSupported = PrintCapabilityPolicy.SupportsDuplexing(
                capabilities.DuplexingCapability.Select(x => x.ToString()).ToArray(), duplex.ToString());
            if (!duplexSupported ||
                !capabilities.PageMediaSizeCapability.Any(x => x.PageMediaSizeName == paper))
                return 4; // Unsupported by the actual driver; never silently downgrade.

            var ticket = new PrintTicket
            {
                CopyCount = plan.Copies,
                PageMediaSize = new PageMediaSize(paper)
            };
            if (capabilities.DuplexingCapability.Count > 0) ticket.Duplexing = duplex;
            // Read raster pages lazily. Do not decode a 1,000 page job into RAM.
            double width = plan.Paper == PrintPaper.A3 ? 1122.52 : 793.70;
            double height = plan.Paper == PrintPaper.A3 ? 1587.40 : 1122.52;
            var paginator = new PdfRasterPaginator(source, plan.Pages, new Size(width, height));
            XpsDocumentWriter writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(paginator, ticket);
            Console.Write("submitted");
            return 0;
        }
        catch
        {
            // Printer driver exceptions may contain network shares and document names.
            // Never expose them in stdout/stderr to a separate untrusted process.
            return 5;
        }
    }

    private sealed record PrintRequest(string PdfPath, string PrinterName,
        int SourcePageCount, string PageRange, int Copies, PrintDuplex Duplex, PrintPaper Paper);

    private sealed class PdfRasterPaginator : DocumentPaginator
    {
        private readonly Stream _source;
        private readonly IReadOnlyList<int> _pages;
        private Size _pageSize;

        public PdfRasterPaginator(Stream source, IReadOnlyList<int> pages, Size pageSize)
        {
            _source = source;
            _pages = pages;
            _pageSize = pageSize;
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _pages.Count;
        public override Size PageSize { get => _pageSize; set => _pageSize = value; }
        public override IDocumentPaginatorSource Source => null!;

        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber < 0 || pageNumber >= _pages.Count)
                return DocumentPage.Missing;

            int index = _pages[pageNumber] - 1;
            _source.Position = 0;
            var points = Conversion.GetPageSize(_source, page: index, leaveOpen: true);
            var raster = PdfPrintRasterPlan.GetRasterSize(points.Width, points.Height);
            _source.Position = 0;
            using var png = new MemoryStream();
            Conversion.SavePng(png, _source, page: index, leaveOpen: true,
                options: new PDFtoImage.RenderOptions(Width: raster.Width, Height: raster.Height,
                    WithAnnotations: true, WithFormFill: true));
            png.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = png;
            bitmap.EndInit();
            bitmap.Freeze();

            const double margin = 18; // WPF DIP, reserved for physical print margins.
            var surface = new FixedPage { Width = _pageSize.Width, Height = _pageSize.Height };
            double areaWidth = Math.Max(1, _pageSize.Width - margin * 2);
            double areaHeight = Math.Max(1, _pageSize.Height - margin * 2);
            // A landscape page on portrait paper (or vice versa) is turned 90° to fill the
            // sheet instead of printing shrunk to a strip.
            bool rotate = PdfPrintRasterPlan.ShouldRotate(
                points.Width, points.Height, _pageSize.Width, _pageSize.Height);
            var image = new Image
            {
                Source = bitmap,
                Width = rotate ? areaHeight : areaWidth,
                Height = rotate ? areaWidth : areaHeight,
                Stretch = Stretch.Uniform,
                LayoutTransform = rotate ? new RotateTransform(90) : Transform.Identity
            };
            FixedPage.SetLeft(image, margin);
            FixedPage.SetTop(image, margin);
            surface.Children.Add(image);
            surface.Measure(_pageSize);
            surface.Arrange(new Rect(_pageSize));
            surface.UpdateLayout();
            return new DocumentPage(surface, _pageSize,
                new Rect(_pageSize), new Rect(_pageSize));
        }
    }
}
