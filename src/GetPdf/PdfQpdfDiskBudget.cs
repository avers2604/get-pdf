namespace GetPdf;

/// <summary>
/// Conservative disk-space preflight for native qpdf intermediate outputs.
/// No disk blocks are reserved; free space must be rechecked before each
/// merge/copy. Output may grow beyond its source size, so assume 2x input.
/// </summary>
internal static class PdfQpdfDiskBudget
{
    internal const long ReservedFreeBytes = 64L * 1024 * 1024;

    public static long EstimateOutputBytes(IEnumerable<long> sourceLengths)
    {
        ArgumentNullException.ThrowIfNull(sourceLengths);
        long total = 0;
        int count = 0;
        foreach (long length in sourceLengths)
        {
            if (length <= 0)
                throw new InvalidDataException("OCR-фрагмент пуст или повреждён.");
            count++;
            try { total = checked(total + length); }
            catch (OverflowException ex)
            {
                throw new InvalidOperationException("Объём OCR-файлов слишком велик.", ex);
            }
        }
        if (count == 0)
            throw new ArgumentException("Нет файлов для оценки места.", nameof(sourceLengths));
        try { return checked(total * 2); }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("Оценка места OCR превышает допустимые значения.", ex);
        }
    }

    public static void RequireAvailableSpace(
        string targetDirectory,
        IReadOnlyList<string> inputFiles,
        Func<string, long>? availableBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentNullException.ThrowIfNull(inputFiles);
        var target = Path.GetFullPath(targetDirectory);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException("Каталог временной сборки PDF отсутствует.");
        long estimate = EstimateOutputBytes(inputFiles.Select(path =>
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Фрагмент PDF отсутствует.", path);
            return info.Length;
        }));
        long free = availableBytes is null
            ? new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace
            : availableBytes(target);
        if (free < 0 || free < estimate || free - estimate < ReservedFreeBytes)
            throw new IOException("Недостаточно свободного места для временной сборки PDF. " +
                "Освободите место на диске или сократите диапазон OCR. Оригинал не изменён.");
    }
}
