using System.Globalization;

namespace GetPdf;

/// <summary>
/// IT-provisioned, offline qpdf page merger for large OCR results. Invokes a
/// fixed executable and uses ArgumentList; never invokes cmd.exe or PowerShell.
/// It operates only on already-verified OCR chunks and an uncommitted staging
/// output. The caller owns final publication and cleanup.
/// </summary>
internal static class PdfQpdfChunkAssembler
{
    internal static IReadOnlyList<string> BuildMergeArguments(
        IReadOnlyList<string> sources, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            throw new ArgumentException("Нет фрагментов для сборки.", nameof(sources));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var files = sources.Select(Path.GetFullPath).ToArray();
        var output = Path.GetFullPath(outputPath);
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new ArgumentException("Нельзя повторно использовать один OCR-фрагмент.", nameof(sources));
        if (files.Any(path => string.Equals(path, output, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Нельзя перезаписывать OCR-фрагмент.");
        var args = new List<string>(4 + files.Length * 2) { "--empty", "--pages" };
        foreach (var file in files)
        {
            args.Add(file);
            args.Add("1-z");
        }
        args.Add("--");
        args.Add(output);
        // Windows CreateProcess has a finite command-line length even without
        // cmd.exe. Conservative bound includes quotes and escaped slashes.
        var estimatedChars = args.Sum(a => 2L * (a.Length + 4)) + 128;
        if (estimatedChars > PdfQpdfMergePlanner.MaxWindowsCommandChars)
            throw new InvalidOperationException(
                "Слишком длинная строка запуска qpdf; требуется пакетное объединение.");
        return args.AsReadOnly();
    }

    internal static int ParsePageCount(string output)
    {
        if (!int.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                out var value) || value < 1)
            throw new InvalidDataException("qpdf вернул некорректное число страниц.");
        return value;
    }

    public static async Task MergeAndVerifyAsync(
        IReadOnlyList<string> sources, string stagingPath, int expectedPages,
        CancellationToken cancellation = default, string? scratchRoot = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!PdfQpdfValidator.IsAvailable)
            throw new InvalidOperationException("Одобренная офлайн-утилита qpdf отсутствует.");
        if (expectedPages < 1) throw new ArgumentOutOfRangeException(nameof(expectedPages));
        // Check paths without creating potentially enormous command lines.
        foreach (var file in sources)
            if (!File.Exists(file))
                throw new FileNotFoundException("OCR-фрагмент отсутствует.", file);

        await PdfQpdfMergeCoordinator.MergeAsync(
            sources, stagingPath,
            async (group, tempOutput, token) =>
            {
                var args = BuildMergeArguments(group, tempOutput);
                var (code, _) = await ExecuteAsync(args, token);
                if (code != 0)
                    throw new InvalidDataException(
                        "qpdf не смог объединить OCR-фрагменты. Итоговый файл не публикуется.");
                await VerifyStructureAsync(tempOutput, token);
            }, cancellation, scratchRoot);
        await VerifyAsync(stagingPath, expectedPages, cancellation);
    }

    public static async Task VerifyAsync(
        string filePath, int expectedPages, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (expectedPages < 1) throw new ArgumentOutOfRangeException(nameof(expectedPages));
        var source = Path.GetFullPath(filePath);
        if (!File.Exists(source) || new FileInfo(source).Length == 0)
            throw new InvalidDataException("Выходной PDF отсутствует или пуст.");
        await VerifyStructureAsync(source, cancellation);
        var (countExit, standardOut) = await ExecuteAsync(["--show-npages", source], cancellation);
        if (countExit != 0 || ParsePageCount(standardOut) != expectedPages)
            throw new InvalidDataException(
                $"qpdf не подтвердил ожидаемое число страниц ({expectedPages}).");
    }

    private static async Task VerifyStructureAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException("qpdf не создал проверяемый PDF.");
        var (checkExit, _) = await ExecuteAsync(["--check", path], cancellation);
        if (checkExit != 0)
            throw new InvalidDataException("qpdf не подтвердил структуру PDF-фрагмента.");
    }

    private static Task<(int ExitCode, string StandardOut)> ExecuteAsync(
        IReadOnlyList<string> args, CancellationToken token) =>
        PdfQpdfProcessRunner.RunAsync(args, token);
}
