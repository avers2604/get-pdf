using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GetDocument.Core.Workspace;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class CalcPivotAndChoiceListTests
{
    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task PivotUsesSeparateSheetAndListValidationSurvivesXlsxSave()
    {
        if (!LibreOfficeEditor.IsSupported)
            Assert.Inconclusive("Необходимы Windows и встроенный LibreOffice/UNO.");

        string folder = Path.Combine(Path.GetTempPath(), "gd-pivot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "Ведомость.xlsx");
        try
        {
            OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, source);
            using (var package = SpreadsheetDocument.Open(source, true))
            {
                var sheet = package.WorkbookPart!.WorksheetParts.Single().Worksheet;
                var data = sheet.GetFirstChild<SheetData>()!;
                data.Append(new Row(TextCell("A1", "Маршрут"), TextCell("B1", "Выпуск")) { RowIndex = 1U });
                data.Append(new Row(TextCell("A2", "18"), NumberCell("B2", 2)) { RowIndex = 2U });
                data.Append(new Row(TextCell("A3", "47"), NumberCell("B3", 3)) { RowIndex = 3U });
                data.Append(new Row(TextCell("A4", "18"), NumberCell("B4", 4)) { RowIndex = 4U });
                sheet.Save();
            }

            EditorDocument editor = await LibreOfficeEditor.Shared.OpenAsync(source);
            try
            {
                await SelectAsync(editor, "C2:C10");
                await editor.ExecuteFeatureAsync("calc-validate-choice-list", ["В работе", "Готово"]);
                await SelectAsync(editor, "A1:B4");
                await editor.ExecuteFeatureAsync("calc-pivot-sum");
                await editor.SaveAsync();
            }
            finally { await editor.CloseAsync(); }

            using var output = SpreadsheetDocument.Open(source, false);
            var sheetNames = output.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>()
                .Select(x => x.Name?.Value).ToArray();
            Assert.AreEqual(2, sheetNames.Length, "Сводная добавляет новый лист, не заменяет исходный.");
            CollectionAssert.Contains(sheetNames, "Лист1");
            Assert.IsTrue(sheetNames.Any(x => x is not null && x.StartsWith("ГЭТ Сводная", StringComparison.Ordinal)));

            var originalPart = output.WorkbookPart.GetPartById(
                output.WorkbookPart.Workbook.Sheets!.Elements<Sheet>().Single(x => x.Name == "Лист1").Id!.Value!)
                as WorksheetPart;
            Assert.IsNotNull(originalPart);
            Assert.IsTrue(originalPart.Worksheet.Descendants<DataValidation>().Any(x =>
                x.Type?.Value == DataValidationValues.List), "Список должен сохраниться как XLSX DataValidation.");
            var rowA2 = originalPart.Worksheet.Descendants<Cell>().Single(x => x.CellReference == "A2");
            Assert.IsNotNull(rowA2, "Первая исходная категория не удалена.");

            using var zip = ZipFile.OpenRead(source);
            Assert.IsTrue(zip.Entries.Any(x => x.FullName.StartsWith("xl/pivotTables/pivotTable",
                StringComparison.OrdinalIgnoreCase)), "Сводная должна сохраниться как редактируемый PivotTable.");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    private static Task SelectAsync(EditorDocument document, string address) =>
        document.DispatchAsync(".uno:GoToCell",
            new Dictionary<string, object> { ["ToPoint"] = address }, focus: false);

    private static Cell TextCell(string address, string text) =>
        new(new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(text)))
        {
            CellReference = address,
            DataType = CellValues.InlineString
        };

    private static Cell NumberCell(string address, int value) =>
        new(new CellValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            CellReference = address,
            DataType = CellValues.Number
        };
}
