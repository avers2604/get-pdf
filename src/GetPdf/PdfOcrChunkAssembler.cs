using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GetPdf;

/// <summary>
/// Reassembles NAPS2-generated OCR PDFs in source page order. It copies page
/// objects, so recognized text streams remain PDF content rather than being
/// rasterized a second time. No source is overwritten.
/// </summary>
internal static class PdfOcrChunkAssembler
{
    public static void Assemble(IReadOnlyList<string> chunkFiles, string outputPath,
        int expectedPageCount, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(chunkFiles);
        if (chunkFiles.Count < 1) throw new ArgumentException("Нет готовых OCR-фрагментов.", nameof(chunkFiles));
        if (expectedPageCount < 1) throw new ArgumentOutOfRangeException(nameof(expectedPageCount));

        var destination = Path.GetFullPath(outputPath);
        var sources = chunkFiles.Select(Path.GetFullPath).ToArray();
        if (sources.Any(source => string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Нельзя сохранять результат вместо фрагмента OCR.");
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new ArgumentException("Повторные OCR-фрагменты не допускаются.", nameof(chunkFiles));

        string folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, ".gupscan-assemble-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var opened = new List<PdfDocument>();
            try
            {
                using var final = new PdfDocument();
                foreach (string path in sources)
                {
                    cancellation.ThrowIfCancellationRequested();
                    PdfDocument source;
                    try { source = PdfReader.Open(path, PdfDocumentOpenMode.Import); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        throw new InvalidDataException("Не удалось прочитать подготовленный OCR-фрагмент.", ex);
                    }
                    opened.Add(source);
                    if (source.PageCount == 0)
                        throw new InvalidDataException("OCR-фрагмент не содержит страниц.");
                    PdfStructureGuard.EnsureSafeToRewrite(source);
                    foreach (var page in source.Pages)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        final.AddPage(page);
                    }
                    if (final.PageCount > expectedPageCount)
                        throw new InvalidDataException("В OCR-фрагментах больше страниц, чем в оригинале.");
                }

                if (final.PageCount != expectedPageCount)
                    throw new InvalidDataException(
                        $"Ожидалось {expectedPageCount} OCR-страниц, получено {final.PageCount}.");
                cancellation.ThrowIfCancellationRequested();
                final.Save(temp);
            }
            finally
            {
                foreach (var source in opened) source.Dispose();
            }
            cancellation.ThrowIfCancellationRequested();
            PdfOutputVerifier.Verify(temp, expectedPageCount);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* session cleanup on next start */ }
        }
    }
}
