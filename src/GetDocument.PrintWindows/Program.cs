using System.IO;
using System.Printing;
using System.Text.Json;
using System.Security.Cryptography;
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
            if (args.Length is < 1 or > 2) return 2;
            using var server = new LocalPrintServer();
            if (args[0] == "trays" && args.Length == 2)
            {
                string queueName = args[1];
                if (string.IsNullOrWhiteSpace(queueName) ||
                    !server.GetPrintQueues().Any(q => q.Name == queueName)) return 3;
                using var printer = server.GetPrintQueue(queueName);
                var bins = printer.GetPrintCapabilities().InputBinCapability
                    .Where(bin => bin != InputBin.Unknown)
                    .Select(bin => bin.ToString())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                Console.Write(JsonSerializer.Serialize(bins));
                return 0;
            }
            if (args.Length != 1) return 2;
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
                request.Duplex, request.Paper,
                request.Orientation, request.Color, request.PagesPerSheet, request.Tray);
            if (string.IsNullOrWhiteSpace(request.PdfPath) ||
                !Path.IsPathFullyQualified(request.PdfPath) ||
                !request.PdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return 2;

            using var source = new FileStream(request.PdfPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.SequentialScan);
            PdfPreviewPlan.ValidateInputLength(source.Length);
            if (request.ExpectedSha256 is { } expected)
            {
                if (expected.Length != 64 || expected.Any(c =>
                    c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    return 3;
                byte[] actualDigest = SHA256.HashData(source);
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expected), actualDigest))
                    return 3; // Snapshot changed between Avalonia and native print worker.
                source.Position = 0;
            }
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

            if (plan.Orientation != PrintOrientation.Automatic)
            {
                var orientation = plan.Orientation == PrintOrientation.Landscape
                    ? PageOrientation.Landscape : PageOrientation.Portrait;
                if (capabilities.PageOrientationCapability is null ||
                    !capabilities.PageOrientationCapability.Contains(orientation))
                    return 4; // Never claim custom orientation unless driver supports it.
                ticket.PageOrientation = orientation;
            }
            if (plan.Color != PrintColorMode.PrinterDefault)
            {
                var color = plan.Color == PrintColorMode.Color
                    ? OutputColor.Color : OutputColor.Monochrome;
                if (capabilities.OutputColorCapability is null ||
                    !capabilities.OutputColorCapability.Contains(color))
                    return 4;
                ticket.OutputColor = color;
            }
            if (plan.Tray is { } requestedTray)
            {
                if (requestedTray.Option != "InputBin" ||
                    !Enum.TryParse<InputBin>(requestedTray.Value, false, out var bin) ||
                    bin == InputBin.Unknown ||
                    !capabilities.InputBinCapability.Contains(bin))
                    return 4;
                ticket.InputBin = bin;
            }
            if (plan.PagesPerSheet != PrintPagesPerSheet.One)
            {
                int pagesPerSheet = (int)plan.PagesPerSheet;
                if (capabilities.PagesPerSheetCapability is null ||
                    !capabilities.PagesPerSheetCapability.Contains(pagesPerSheet))
                    return 4;
                ticket.PagesPerSheet = pagesPerSheet;
            }
            // Read raster pages lazily. Do not decode a 1,000 page job into RAM.
            double width = plan.Paper == PrintPaper.A3 ? 1122.52 : 793.70;
            double height = plan.Paper == PrintPaper.A3 ? 1587.40 : 1122.52;
            if (plan.Orientation == PrintOrientation.Landscape)
                (width, height) = (height, width);
            // Use the driver's print-ticket-specific imageable area when available.
            // The previous fixed 18-DIP inset could cut off text on some MFPs.
            var sheetSize = new Size(width, height);
            var deviceBounds = queue.GetPrintCapabilities(ticket).PageImageableArea;
            var area = PrintImageableArea.Choose(width, height,
                deviceBounds?.OriginWidth, deviceBounds?.OriginHeight,
                deviceBounds?.ExtentWidth, deviceBounds?.ExtentHeight);
            var printable = new Rect(area.X, area.Y, area.Width, area.Height);
            var paginator = new PdfRasterPaginator(source, plan.Pages, sheetSize, printable);
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
        int SourcePageCount, string PageRange, int Copies, PrintDuplex Duplex, PrintPaper Paper,
        PrintOrientation Orientation, PrintColorMode Color, PrintPagesPerSheet PagesPerSheet,
        PrintTrayOption? Tray, string? ExpectedSha256);

    private sealed class PdfRasterPaginator : DocumentPaginator
    {
        private readonly Stream _source;
        private readonly IReadOnlyList<int> _pages;
        private Size _pageSize;
        private readonly Rect _printableArea;

        public PdfRasterPaginator(Stream source, IReadOnlyList<int> pages, Size pageSize,
            Rect printableArea)
        {
            _source = source;
            _pages = pages;
            _pageSize = pageSize;
            _printableArea = printableArea;
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

            var surface = new FixedPage { Width = _pageSize.Width, Height = _pageSize.Height };
            double areaWidth = _printableArea.Width;
            double areaHeight = _printableArea.Height;
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
            // The printable region is supplied by the actual selected driver.
            // A clip container prevents transformed bitmap overflow into hard margins.
            var printSlot = new Grid
            {
                Width = areaWidth, Height = areaHeight,
                ClipToBounds = true
            };
            printSlot.Children.Add(image);
            FixedPage.SetLeft(printSlot, _printableArea.X);
            FixedPage.SetTop(printSlot, _printableArea.Y);
            surface.Children.Add(printSlot);
            surface.Measure(_pageSize);
            surface.Arrange(new Rect(_pageSize));
            surface.UpdateLayout();
            return new DocumentPage(surface, _pageSize,
                new Rect(_pageSize), _printableArea);
        }
    }
}
