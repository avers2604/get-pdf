namespace GetPdf;

/// <summary>
/// Advisory whole-job disk admission before NAPS2 imports the first page.
/// Estimates OCR chunks, qpdf intermediate copies and publication separately.
/// This is NOT a filesystem reservation or a guaranteed output size bound.
/// Per-stage checks remain mandatory during the actual operation.
/// </summary>
internal static class PdfOcrDiskPreflight
{
    internal const long MinEstimatedBytesPerPage = 2L * 1024 * 1024;

    internal readonly record struct EstimateResult(
        long ExpectedChunkBytes, long ScratchRequiredBytes, long OutputRequiredBytes);

    public static EstimateResult Estimate(int pageCount, long sourceBytes)
    {
        if (pageCount < 1) throw new ArgumentOutOfRangeException(nameof(pageCount));
        if (sourceBytes < 0) throw new ArgumentOutOfRangeException(nameof(sourceBytes));
        try
        {
            long predictedChunks = Math.Max(
                checked(pageCount * MinEstimatedBytesPerPage), sourceBytes);
            // Scratch: OCR chunks plus simultaneous successive qpdf merge levels.
            // Output: final merge/copy/verification staging. Include headroom
            // on both volumes even when they happen to be the same volume.
            long scratch = checked(predictedChunks * 3 + PdfQpdfDiskBudget.ReservedFreeBytes);
            long output = checked(predictedChunks * 2 + PdfQpdfDiskBudget.ReservedFreeBytes);
            return new EstimateResult(predictedChunks, scratch, output);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                "Оценочный размер OCR-документа превышает допустимый объём.", ex);
        }
    }

    public static EstimateResult RequireSufficientSpace(
        int pageCount,
        long sourceBytes,
        string sessionFolder,
        string outputFolder,
        Func<string, long>? availableBytes = null,
        Func<string, string>? volumeIdentifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        var session = Path.GetFullPath(sessionFolder);
        var output = Path.GetFullPath(outputFolder);
        if (!Directory.Exists(session))
            throw new DirectoryNotFoundException("Каталог OCR-сессии не найден.");
        if (!Directory.Exists(output))
            throw new DirectoryNotFoundException("Каталог назначения OCR не найден.");

        var estimate = Estimate(pageCount, sourceBytes);
        volumeIdentifier ??= location => Path.GetPathRoot(location)!;
        availableBytes ??= location =>
            new DriveInfo(Path.GetPathRoot(location)!).AvailableFreeSpace;

        var sessionVolume = volumeIdentifier(session);
        var outputVolume = volumeIdentifier(output);
        if (string.IsNullOrWhiteSpace(sessionVolume) || string.IsNullOrWhiteSpace(outputVolume))
            throw new IOException("Не удалось определить диск для подготовки OCR.");
        if (string.Equals(sessionVolume, outputVolume, StringComparison.OrdinalIgnoreCase))
        {
            long needed = checked(estimate.ScratchRequiredBytes + estimate.OutputRequiredBytes);
            EnsureEnough(needed, availableBytes(session));
        }
        else
        {
            EnsureEnough(estimate.ScratchRequiredBytes, availableBytes(session));
            EnsureEnough(estimate.OutputRequiredBytes, availableBytes(output));
        }
        return estimate;
    }

    private static void EnsureEnough(long needed, long available)
    {
        if (available < needed)
            throw new PdfOcrInsufficientSpaceException(needed, available);
    }
}

internal sealed class PdfOcrInsufficientSpaceException : IOException
{
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }

    public PdfOcrInsufficientSpaceException(long requiredBytes, long availableBytes)
        : base("Недостаточно свободного места для OCR: требуется ориентировочно " +
            $"{RoundUpMiB(requiredBytes)} МиБ, доступно " +
            $"{Math.Max(0, availableBytes) / (1024L * 1024)} МиБ. " +
            "Освободите место или сократите число страниц. Исходный PDF не изменён.")
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    private static long RoundUpMiB(long bytes) =>
        bytes / (1024L * 1024) + (bytes % (1024L * 1024) == 0 ? 0 : 1);
}
