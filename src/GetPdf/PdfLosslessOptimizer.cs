namespace GetPdf;

/// <summary>
/// Lossless stream/object recompression using a separately IT-approved qpdf binary.
/// Never modifies the original or an existing destination; never processes signatures.
/// </summary>
internal static class PdfLosslessOptimizer
{
    internal static IReadOnlyList<string> Arguments(string input, string stage) =>
    [
        "--stream-data=compress",
        "--object-streams=generate",
        Path.GetFullPath(input), Path.GetFullPath(stage)
    ];

    public static async Task<(long Before, long After)> OptimizeAsync(
        string input, string destination, int expectedPages, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!PdfQpdfValidator.IsAvailable)
            throw new InvalidOperationException("Утверждённая ИТ-службой утилита qpdf недоступна.");
        string source = Path.GetFullPath(input), target = Path.GetFullPath(destination);
        if (!PdfSaveSafety.TryValidateNewCopy(target, [source], out var issue))
            throw new InvalidOperationException(issue);
        PdfStructureGuard.EnsureSafeToRewrite(source);
        if (expectedPages is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(expectedPages));

        long before = new FileInfo(source).Length;
        string directory = Path.GetDirectoryName(target)!;
        string stage = Path.Combine(directory,
            ".getpdf-optimize-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var (code, _) = await PdfQpdfProcessRunner.RunAsync(Arguments(source, stage), token);
            if (!PdfQpdfProcessRunner.Succeeded(code))
                throw new InvalidDataException("Не удалось выполнить безопасную оптимизацию qpdf.");
            token.ThrowIfCancellationRequested();
            PdfOutputVerifier.Verify(stage, expectedPages);
            await PdfQpdfValidator.ValidateIfAvailableAsync(stage, token);

            // Verify the text layer and page order before publication.
            var report = await Task.Run(() =>
                PdfTextComparisonService.CompareFiles(source, stage, token), token);
            if (report.Changes.Any(item => item.Type is
                PdfTextComparisonService.Kind.Added or PdfTextComparisonService.Kind.Removed or
                PdfTextComparisonService.Kind.ChangedText))
                throw new InvalidDataException(
                    "После оптимизации изменился текст или порядок страниц; файл не сохраняется.");

            long after = new FileInfo(stage).Length;
            if (after >= before)
                throw new InvalidOperationException(
                    "Оптимизация не уменьшила размер PDF. Исходник оставлен без изменений.");
            token.ThrowIfCancellationRequested();
            File.Move(stage, target, overwrite: false);
            return (before, after);
        }
        finally
        {
            try { if (File.Exists(stage)) File.Delete(stage); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { DiagnosticLog.Failure("pdf-optimize-cleanup", ex); }
        }
    }
}
