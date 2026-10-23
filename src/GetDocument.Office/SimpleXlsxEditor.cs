using System.Security.Cryptography;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace GetDocument.Office;

/// <summary>
/// Safely edits one deliberately simple worksheet: up to A1:J10, inline text only.
/// Complex workbooks (styles, formulas, extra parts, external links, merges) are rejected.
/// Saves never overwrite originals; this is NOT an Excel replacement.
/// </summary>
public sealed record SimpleXlsxSnapshot(string[][] Cells, string Sha256);

public static class SimpleXlsxEditor
{
    public const int RowCount = 10;
    public const int ColumnCount = 10;
    private const int MaxCellChars = 2048;
    private const long MaxSourceBytes = 8L * 1024 * 1024;

    public static SimpleXlsxSnapshot Load(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = Path.GetFullPath(sourcePath);
        if (!Path.GetExtension(source).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ожидается XLSX-файл.", nameof(sourcePath));
        if (new FileInfo(source).Length is <= 0 or > MaxSourceBytes)
            throw new InvalidDataException("XLSX пуст либо превышает лимит упрощённого редактора 8 МиБ.");

        using var document = SpreadsheetDocument.Open(source, false);
        var sheetData = ValidateSimpleSheet(document);
        var values = EmptyGrid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in sheetData.Elements<Row>())
        {
            if (row.RowIndex is null || row.RowIndex.Value is < 1 or > RowCount ||
                row.ChildElements.Any(x => x is not Cell))
                throw new NotSupportedException("Сложная структура строк XLSX не поддерживается.");
            foreach (var cell in row.Elements<Cell>())
            {
                string address = cell.CellReference?.Value ?? "";
                if (address.Length is < 2 or > 3 ||
                    address[0] is < 'A' or > 'J' ||
                    !int.TryParse(address[1..], out int number) ||
                    number != row.RowIndex.Value || number is < 1 or > RowCount ||
                    !seen.Add(address) || cell.StyleIndex is not null ||
                    cell.DataType?.Value != CellValues.InlineString ||
                    cell.ChildElements.Count != 1 ||
                    cell.FirstChild is not InlineString inline ||
                    inline.ChildElements.Count != 1 ||
                    inline.FirstChild is not Text t)
                    throw new NotSupportedException("Формулы, форматирование и нестандартные ячейки XLSX не поддерживаются.");
                values[number - 1][address[0] - 'A'] = t.Text ?? "";
            }
        }
        ValidateValues(values);
        return new SimpleXlsxSnapshot(values, Hash(source));
    }

    public static void SaveNewRevision(string sourcePath, string expectedSha256,
        string destination, string[][] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ValidateValues(values);
        string source = Path.GetFullPath(sourcePath);
        string target = Path.GetFullPath(destination);
        if (!Path.GetExtension(source).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(target).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Источник и новая редакция должны иметь формат XLSX.");
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, target, comparer) || File.Exists(target))
            throw new IOException("Перезапись существующего XLSX запрещена.");
        string directory = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Каталог назначения отсутствует.");

        var original = Load(source);
        if (!string.Equals(original.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("XLSX изменён другим пользователем; перечитайте файл.");

        string temporary = Path.Combine(directory, ".get-xlsx-revision-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            File.Copy(source, temporary);
            if (!string.Equals(Hash(temporary), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Источник XLSX изменён во время копирования.");
            using (var document = SpreadsheetDocument.Open(temporary, true))
            {
                var data = ValidateSimpleSheet(document);
                data.RemoveAllChildren<Row>();
                for (int y = 0; y < RowCount; y++)
                {
                    var row = new Row { RowIndex = (uint)(y + 1) };
                    for (int x = 0; x < ColumnCount; x++)
                    {
                        string value = values[y][x];
                        if (value.Length == 0) continue;
                        row.Append(new Cell(new InlineString(new Text(value)
                            { Space = SpaceProcessingModeValues.Preserve }))
                        {
                            CellReference = $"{(char)('A' + x)}{y + 1}",
                            DataType = CellValues.InlineString
                        });
                    }
                    if (row.HasChildren) data.Append(row);
                }
                document.WorkbookPart!.WorksheetParts.Single().Worksheet.Save();
            }
            var confirmed = Load(temporary);
            for (int y = 0; y < RowCount; y++)
                for (int x = 0; x < ColumnCount; x++)
                    if (confirmed.Cells[y][x] != values[y][x])
                        throw new InvalidDataException("Не удалось подтвердить данные новой редакции XLSX.");
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static SheetData ValidateSimpleSheet(SpreadsheetDocument document)
    {
        var book = document.WorkbookPart;
        var worksheets = book?.WorksheetParts.ToArray();
        if (book is null || book.Workbook is null || worksheets is not { Length: 1 } ||
            book.Parts.Count() != 1 || book.ExternalRelationships.Any() ||
            book.Workbook.ChildElements.Count != 1 ||
            book.Workbook.FirstChild is not Sheets sheetList ||
            sheetList.ChildElements.Count != 1 ||
            sheetList.FirstChild is not Sheet ||
            worksheets[0].Parts.Any() || worksheets[0].ExternalRelationships.Any())
            throw new NotSupportedException("XLSX содержит дополнительные листы, связи или сложные элементы.");
        var sheet = worksheets[0].Worksheet;
        if (sheet is null || sheet.ChildElements.Count != 1 ||
            sheet.FirstChild is not SheetData data ||
            data.ChildElements.Any(x => x is not Row))
            throw new NotSupportedException("Оформление, объединения и дополнительные элементы листа запрещены.");
        return data;
    }

    private static string[][] EmptyGrid() => Enumerable.Range(0, RowCount)
        .Select(_ => new string[ColumnCount]).Select(row => row.Select(_ => "").ToArray()).ToArray();

    private static void ValidateValues(string[][] values)
    {
        if (values is null || values.Length != RowCount ||
            values.Any(row => row is null || row.Length != ColumnCount))
            throw new ArgumentException("Таблица должна содержать ровно 10×10 ячеек.", nameof(values));
        foreach (var row in values)
            foreach (var value in row)
            {
                if (value is null || value.Length > MaxCellChars)
                    throw new ArgumentException("Значение ячейки отсутствует или слишком длинное.", nameof(values));
                try { XmlConvert.VerifyXmlChars(value); }
                catch (XmlException ex)
                { throw new ArgumentException("Некорректные XML-символы в ячейке.", nameof(values), ex); }
            }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
