using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace GetDocument.Office.Spreadsheets;

/// <summary>
/// A bounded preview of the first worksheet only. No edits, macros, formulas, links
/// or XLS-to-XLSX conversion. Native Office applications are NOT launched.
/// </summary>
public sealed record SpreadsheetPreview(
    string SheetName, int VisibleColumns, IReadOnlyList<string[]> Rows, bool IsTruncated);

public static class ReadOnlySpreadsheetPreview
{
    public const int DefaultMaxRows = 100;
    public const int DefaultMaxColumns = 20;
    public const int MaxDisplayCellChars = 256;
    public const long MaxInputBytes = 32L * 1024 * 1024;

    // Excel BIFF2-5 may contain DOS/Windows codepages; without the provider
    // this can fail on non-Windows .NET even if OOXML XLSX works.
    private static readonly Lazy<bool> CodePagesReady = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    public static SpreadsheetPreview Read(string path,
        int maxRows = DefaultMaxRows, int maxColumns = DefaultMaxColumns,
        CancellationToken cancellationToken = default)
    {
        if (maxRows is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        if (maxColumns is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(maxColumns));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not (".xls" or ".xlsb" or ".xlsx"))
            throw new NotSupportedException("Предпросмотр поддерживает XLS, XLSB и XLSX.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Файл Excel не найден или недоступен.", fullPath);

        cancellationToken.ThrowIfCancellationRequested();
        _ = CodePagesReady.Value;
        using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxInputBytes)
            throw new InvalidDataException("Excel-файл пуст либо превышает лимит 32 МиБ.");

        // SinglePassMode avoids ExcelDataReader's full RowCount pre-scan.
        // Never call AsDataSet(): that materializes the entire workbook.
        using var reader = ExcelReaderFactory.CreateReader(input, new ExcelReaderConfiguration
        {
            SinglePassMode = true,
            LeaveOpen = false
        });
        cancellationToken.ThrowIfCancellationRequested();
        // In SinglePassMode FieldCount before the first Read() comes from the sheet's
        // <dimension> hint, which many generators omit or leave stale ("A1"); a row can be
        // wider. Take the width of every row actually read and pad rows to the widest one.
        int fields = 0;
        var rows = new List<string[]>(Math.Min(maxRows, 100));
        while (rows.Count < maxRows && reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            fields = Math.Max(fields, reader.FieldCount);
            int width = Math.Min(maxColumns, Math.Max(0, reader.FieldCount));
            var values = new string[width];
            for (int column = 0; column < width; column++)
                values[column] = FormatCell(reader.GetValue(column));
            rows.Add(values);
        }
        int columns = Math.Min(maxColumns, fields);
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Length < columns)
            {
                var padded = new string[columns];
                Array.Fill(padded, "");
                rows[i].CopyTo(padded, 0);
                rows[i] = padded;
            }
        bool moreRows = reader.Read();
        cancellationToken.ThrowIfCancellationRequested();
        bool truncated = moreRows || fields > maxColumns || reader.ResultsCount > 1;
        string name = string.IsNullOrWhiteSpace(reader.Name) ? "Лист1" : FormatCell(reader.Name);
        return new SpreadsheetPreview(name, columns, rows, truncated);
    }

    public static string FormatCell(object? value)
    {
        string input = value switch
        {
            null or DBNull => "",
            bool b => b ? "Истина" : "Ложь",
            DateTime dt => dt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
            TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => value.ToString() ?? ""
        };
        var output = new StringBuilder(Math.Min(input.Length, MaxDisplayCellChars + 1));
        bool lastBlank = false;
        int written = 0;
        foreach (char ch in input)
        {
            if (written == MaxDisplayCellChars)
            {
                output.Append('…');
                break;
            }
            bool unsafeChar = char.IsControl(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format;
            char safe = unsafeChar ? ' ' : ch;
            if (unsafeChar && lastBlank) continue;
            output.Append(safe);
            written++;
            lastBlank = safe == ' ';
        }
        return output.ToString();
    }
}
