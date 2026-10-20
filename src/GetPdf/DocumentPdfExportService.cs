using NAPS2.Images;
using NAPS2.Ocr;
using NAPS2.Pdf;
using NAPS2.Scan;

namespace GetPdf;

/// <summary>
/// Shared PDF/OCR pipeline for Save As and the explicit classic-Outlook draft.
/// The caller controls the destination, user interaction and cleanup of temporary files.
/// </summary>
internal static class DocumentPdfExportService
{
    internal static async Task ExportWorkspaceAsync(ScanningContext context, ScanProfile? profile,
        string destination, IEnumerable<OperatorPage> pages, CancellationToken cancellation = default)
    {
        var all = pages.ToArray();
        if (all.All(p => p.PdfSource is null))
        { await ExportAsync(context, profile, destination, all.Select(p => p.Source), cancellation); return; }
        if (profile?.PdfCompat is "PdfA2B" or "PdfA3B")
            throw new NotSupportedException("Для смешанного документа с исходными PDF выберите обычный PDF: соответствие PDF/A после объединения не подтверждено.");
        string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".mixed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var rasterPages = await Task.Run(() => SelectRasterPages(all.Select(p => p.PdfSource).ToArray(),
                profile?.OcrEnabled == true, cancellation), cancellation);
            var raster = all.Where((p, index) => rasterPages[index]).ToArray();
            string scanPdf = Path.Combine(folder, "scanned.pdf");
            if (raster.Length > 0) await ExportAsync(context, profile, scanPdf, raster.Select(p => p.Source), cancellation);
            int rasterIndex = 0;
            var assembly = all.Select((p, index) => rasterPages[index] ? new PdfSourcePageReference(scanPdf, rasterIndex++) : p.PdfSource!).ToArray();
            await Task.Run(() => PdfDocumentWriter.SaveMixed(destination, assembly, cancellation), cancellation);
        }
        finally
        {
            foreach (string path in Directory.EnumerateFiles(folder)) File.Delete(path);
            Directory.Delete(folder);
        }
    }
    internal static bool[] SelectRasterPages(PdfSourcePageReference?[] pages, bool ocr, CancellationToken cancellation)
    {
        var result = pages.Select(p => p is null).ToArray();
        if (!ocr) return result;
        foreach (var source in pages.Select((p, i) => (Page: p, Index: i)).Where(x => x.Page is not null)
                     .GroupBy(x => x.Page!.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            using var document = UglyToad.PdfPig.PdfDocument.Open(source.Key);
            foreach (var entry in source)
            {
                cancellation.ThrowIfCancellationRequested();
                result[entry.Index] = string.IsNullOrWhiteSpace(document.GetPage(entry.Page!.SourcePage + 1).Text);
            }
        }
        return result;
    }

    internal static PdfExportParams CreatePdfParams(ScanProfile? profile) =>
        new() { Compat = profile?.PdfCompat switch
        {
            "PdfA2B" => PdfCompat.PdfA2B,
            "PdfA3B" => PdfCompat.PdfA3B,
            _ => PdfCompat.Default
        } };

    internal static OcrParams? CreateOcrParams(ScanProfile? profile) =>
        profile?.OcrEnabled == true ? new OcrParams("rus+eng") : null;

    internal static void ConfigureOcr(ScanningContext context, ScanProfile? profile,
        string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (profile?.OcrEnabled != true) return;
        var tessdata = Path.Combine(applicationDirectory, "tessdata");
        if (!File.Exists(Path.Combine(tessdata, "rus.traineddata")) ||
            !File.Exists(Path.Combine(tessdata, "eng.traineddata")))
            throw new InvalidOperationException("Для OCR установите утверждённые файлы rus.traineddata и eng.traineddata в папку tessdata рядом с приложением.");
        context.OcrEngine = TesseractOcrEngine.Bundled(tessdata);
    }

    internal static async Task ExportAsync(ScanningContext context, ScanProfile? profile,
        string destination, IEnumerable<ProcessedImage> pages, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(pages);
        var sourcePages = pages.ToArray();
        if (sourcePages.Length == 0) throw new InvalidOperationException("В документе нет страниц.");
        cancellation.ThrowIfCancellationRequested();
        ConfigureOcr(context, profile, AppContext.BaseDirectory);
        var exporter = new PdfExporter(context);
        if (!await exporter.Export(destination, sourcePages,
                exportParams: CreatePdfParams(profile), ocrParams: CreateOcrParams(profile)))
            throw new IOException("Создание PDF было отменено.");
        // NAPS2 export may not observe cancellation mid-call. Never publish an
        // incomplete/cancelled result; the caller owns staging-file cleanup.
        cancellation.ThrowIfCancellationRequested();
        PdfOutputVerifier.Verify(destination, sourcePages.Length);
        cancellation.ThrowIfCancellationRequested();
    }
}
