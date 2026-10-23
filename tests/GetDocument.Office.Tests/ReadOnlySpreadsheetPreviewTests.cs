using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GetDocument.Core.Workspace;
using GetDocument.Office;
using GetDocument.Office.Spreadsheets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class ReadOnlySpreadsheetPreviewTests
{
    [TestMethod]
    public void ReadsRealXlsxInReadOnlyModeWithoutChangingOriginalBytes()
    {
        using var folder = TemporaryFolder.Create();
        string path = Path.Combine(folder.Path, "Транспорт.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, path);
        using (var workbook = SpreadsheetDocument.Open(path, true))
        {
            var worksheet = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet;
            var data = worksheet.GetFirstChild<SheetData>()!;
            var row = new Row { RowIndex = 1U };
            row.Append(new Cell(new InlineString(new Text("ГЭТ 🚋")))
            {
                CellReference = "A1",
                DataType = CellValues.InlineString
            });
            row.Append(new Cell { CellReference = "B1", CellValue = new CellValue("42"), DataType = CellValues.Number });
            data.Append(row);
            worksheet.Save();
        }
        byte[] original = File.ReadAllBytes(path);
        var preview = ReadOnlySpreadsheetPreview.Read(path);
        Assert.AreEqual(1, preview.Rows.Count);
        Assert.AreEqual("ГЭТ 🚋", preview.Rows[0][0]);
        Assert.AreEqual("42", preview.Rows[0][1]);
        Assert.IsFalse(preview.IsTruncated);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void BlankWorkbookHasNoPreviewRows()
    {
        using var folder = TemporaryFolder.Create();
        string path = Path.Combine(folder.Path, "blank.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, path);
        var preview = ReadOnlySpreadsheetPreview.Read(path);
        Assert.AreEqual(0, preview.Rows.Count);
        Assert.IsFalse(preview.IsTruncated);
    }

    [TestMethod]
    public void RefusesFilesOfOtherExtensionsAndMissingPaths()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ReadOnlySpreadsheetPreview.Read("malware.exe"));
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            ReadOnlySpreadsheetPreview.Read(Path.Combine(Path.GetTempPath(),
                Guid.NewGuid().ToString("N") + ".xlsb")));
    }

    [TestMethod]
    public void SanitizesControlAndBidiCharactersForReadOnlyDisplay()
    {
        Assert.AreEqual("abc def", ReadOnlySpreadsheetPreview.FormatCell("abc\u202E\u0001def"));
        Assert.AreEqual("42", ReadOnlySpreadsheetPreview.FormatCell(42));
        Assert.AreEqual("Истина", ReadOnlySpreadsheetPreview.FormatCell(true));
    }

    [TestMethod]
    public void TruncatesLongCellDisplayWithoutRemovingOriginalFileContents()
    {
        string formatted = ReadOnlySpreadsheetPreview.FormatCell(new string('X', 3000));
        Assert.IsTrue(formatted.EndsWith("…", StringComparison.Ordinal));
        Assert.IsTrue(formatted.Length <= ReadOnlySpreadsheetPreview.MaxDisplayCellChars + 1);
    }

    [TestMethod]
    public void EnforcesPreviewBounds()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ReadOnlySpreadsheetPreview.Read("any.xlsx", maxRows: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ReadOnlySpreadsheetPreview.Read("any.xlsx", maxColumns: 0));
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; }
        private TemporaryFolder(string path) { Path = path; Directory.CreateDirectory(path); }
        public static TemporaryFolder Create() => new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gd06-" + Guid.NewGuid().ToString("N")));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
