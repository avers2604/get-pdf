namespace GetPdf;

/// <summary>
/// Creates ordered, command-line-safe source groups for an offline qpdf merge.
/// One group is passed to one native process; groups are recursively merged
/// until only a single output remains. No Windows command shell is involved.
/// </summary>
internal static class PdfQpdfMergePlanner
{
    internal const int DefaultMaxFilesPerCall = 32;
    internal const int MaxWindowsCommandChars = 30000;

    // Reserve for the executable, fixed options, quoted output path and
    // Windows escaping. Deliberately overestimate each quoted source path.
    private const int FixedCharacters = 128;

    public static IReadOnlyList<IReadOnlyList<string>> Partition(
        IReadOnlyList<string> sourceFiles,
        int maxFilesPerCall = DefaultMaxFilesPerCall,
        int maxCommandChars = MaxWindowsCommandChars)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        if (sourceFiles.Count == 0)
            throw new ArgumentException("Не выбраны OCR-фрагменты.", nameof(sourceFiles));
        if (maxFilesPerCall < 2)
            throw new ArgumentOutOfRangeException(nameof(maxFilesPerCall));
        if (maxCommandChars < FixedCharacters + 1)
            throw new ArgumentOutOfRangeException(nameof(maxCommandChars));

        var sources = sourceFiles.Select(Path.GetFullPath).ToArray();
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new ArgumentException("Один OCR-фрагмент указан повторно.", nameof(sourceFiles));

        var result = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        int length = FixedCharacters;
        foreach (string file in sources)
        {
            // Conservatively account for quotes, slashes and Windows
            // command-line escaping without relying on the actual shell.
            long cost = 2L * (file.Length + 4);
            if (cost > maxCommandChars - FixedCharacters)
                throw new InvalidOperationException(
                    "Слишком длинный путь к OCR-фрагменту для запуска qpdf.");
            if (current.Count > 0 &&
                (current.Count >= maxFilesPerCall || length + cost > maxCommandChars))
            {
                result.Add(current.AsReadOnly());
                current = new List<string>();
                length = FixedCharacters;
            }
            current.Add(file);
            length += checked((int)cost);
        }
        if (current.Count > 0) result.Add(current.AsReadOnly());
        return result.AsReadOnly();
    }
}
