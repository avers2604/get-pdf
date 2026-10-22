namespace GetPdf;

/// <summary>Validated visual page-number labels; not a cryptographic signature.</summary>
internal static class PdfBatchPageLabels
{
    public static string Number(int oneBasedPage, int total, string prefix = "Стр.")
    {
        if (total is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(total));
        if (oneBasedPage < 1 || oneBasedPage > total)
            throw new ArgumentOutOfRangeException(nameof(oneBasedPage));
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 40 || prefix.Any(char.IsControl))
            throw new ArgumentException("Некорректная подпись страниц.", nameof(prefix));
        string result = $"{prefix.Trim()} {oneBasedPage} / {total}";
        if (result.Length > 80) throw new ArgumentException("Слишком длинная подпись страниц.");
        return result;
    }
}

internal enum PdfPageNumberFormat { Number, NumberOfTotal, PageNumberOfTotal }

internal sealed record PdfPageNumberingOptions(
    PdfPageNumberFormat Format, PdfPageNumberPosition Position, int StartAt, bool SkipFirstPage);

/// <summary>
/// Visible page numbers for the export copy. As in Russian office practice (GOST),
/// a skipped title page still counts but shows no number.
/// </summary>
internal static class PdfPageNumbering
{
    public static string? Label(int index, int count, PdfPageNumberingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (count is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(count));
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        if (options.StartAt is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.SkipFirstPage && index == 0) return null;
        int number = options.StartAt + index, total = options.StartAt + count - 1;
        return options.Format switch
        {
            PdfPageNumberFormat.Number => number.ToString(),
            PdfPageNumberFormat.NumberOfTotal => $"{number} из {total}",
            PdfPageNumberFormat.PageNumberOfTotal => $"Страница {number} из {total}",
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
    }
}
/// <summary>Allocates bounded output names without overwriting existing files.</summary>
internal static class PdfSplitPageNaming
{
    internal const int MaxPerOperation = 200;

    internal static string[] Plan(string samplePath, int count)
    {
        if (count is < 1 or > MaxPerOperation)
            throw new ArgumentOutOfRangeException(nameof(count));
        string path = Path.GetFullPath(samplePath);
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Укажите шаблон имени PDF.", nameof(samplePath));
        string folder = Path.GetDirectoryName(path)!;
        string prefix = Path.GetFileNameWithoutExtension(path);
        return Enumerable.Range(1, count).Select(i =>
            Path.Combine(folder, $"{prefix}_{i:D3}.pdf")).ToArray();
    }
}
